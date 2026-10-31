using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Cli;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

// Real native child processes and temporary SQLite; all measurements are synthetic.
public sealed class StructuredExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string First = "@researchlab/v1 {\"type\":\"metric\",\"name\":\"residual\",\"value\":0.5,\"step\":1,\"unit\":\"1\"}\n";
    private const string Second = "@researchlab/v1 {\"type\":\"metric\",\"name\":\"residual\",\"value\":0.2,\"step\":2,\"unit\":\"1\"}\n";

    [Theory]
    [InlineData("metric-basic")]
    [InlineData("metric-split")]
    [InlineData("metric-stderr")]
    [InlineData("metric-eof")]
    public async Task RawStdoutAndOrderedMetricsSurviveRealExecution(string mode)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode);
        var id = await Execution(fixture, catalog).StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var run = (await new SqliteResearchStore(fixture.Database, readOnly: true).GetRunAsync(id, Token))!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(new[] { 0.5, 0.2 }, run.Metrics.Select(point => point.Value));
        Assert.Equal(new long[] { 1, 2 }, run.Metrics.Select(point => point.Sequence));
        Assert.Equal(new long?[] { 1, 2 }, run.Metrics.Select(point => point.Step));
        Assert.All(run.Metrics, point => Assert.Equal(id, point.RunId));
        Assert.Equal(4, run.Version);
        var expected = "Starting...\r\n" + First + Second + "Completed.\n";
        if (mode == "metric-split")
            expected = "Starting...\r\n" + First.Replace("\"1\"", "\"µs\"").Replace("\n", "\r\n") +
                Second.Replace("\"1\"", "\"µs\"") + "Completed.\n";
        if (mode == "metric-eof") expected = "Starting...\r\n" + First + Second.TrimEnd('\n');
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(LogPath(fixture, id, "stdout.log"), Token));
        Assert.Empty(await DiagnosticsAsync(fixture, id));
        if (mode == "metric-stderr")
            Assert.Equal(512 * 1024 + Encoding.UTF8.GetByteCount(First), new FileInfo(LogPath(fixture, id, "stderr.log")).Length);
    }

    [Theory]
    [InlineData("metric-invalid", "InvalidFieldType")]
    [InlineData("metric-unknown", "UnsupportedEventType")]
    [InlineData("metric-unit", "IncompatibleMetricUnit")]
    [InlineData("metric-oversized", "EventTooLarge")]
    public async Task BadEventIsDiagnosedWithoutDamagingGoodNeighbors(string mode, string code)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode);
        var id = await Execution(fixture, catalog).StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var run = (await fixture.Store.GetRunAsync(id, Token))!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(new[] { 0.5, 0.2 }, run.Metrics.Select(point => point.Value));
        Assert.Equal(new long[] { 1, 2 }, run.Metrics.Select(point => point.Sequence));
        var diagnostic = Assert.Single(await DiagnosticsAsync(fixture, id));
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(code, diagnostic.Code);
        Assert.DoesNotContain("private-secret", await File.ReadAllTextAsync(LogPath(fixture, id, "protocol.log"), Token));
    }

    [Fact]
    public async Task DiagnosticsHaveFixedRecordAndByteBounds()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "metric-diagnostics");
        var id = await Execution(fixture, catalog).StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var diagnostics = await DiagnosticsAsync(fixture, id);
        Assert.Equal(257, diagnostics.Length);
        Assert.Equal("DiagnosticsTruncated", diagnostics[^1].Code);
        Assert.True(new FileInfo(LogPath(fixture, id, "protocol.log")).Length < 64 * 1024);
        Assert.Equal(2, (await fixture.Store.GetRunAsync(id, Token))!.Metrics.Count);
    }

    [Theory]
    [InlineData("metric-many")]
    [InlineData("metric-many-stderr")]
    public async Task ManyMetricsAreFlushedInOrderAndHaveOneTerminalVersion(string mode)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode);
        var id = await Execution(fixture, catalog).StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var run = (await fixture.Store.GetRunAsync(id, Token))!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(2000, run.Metrics.Count);
        Assert.Equal(Enumerable.Range(1, 2000).Select(value => (long)value), run.Metrics.Select(point => point.Sequence));
        Assert.Equal(Enumerable.Range(0, 2000).Select(value => (double)value), run.Metrics.Select(point => point.Value));
        Assert.Equal(2002, run.Version);
    }

    [Fact]
    public async Task NonzeroExitRetainsValidMeasurementsInFailedRun()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "metric-fail");
        var id = await Execution(fixture, catalog).StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var run = (await fixture.Store.GetRunAsync(id, Token))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(7, run.ExitCode);
        Assert.Equal(new[] { 0.5, 0.2 }, run.Metrics.Select(point => point.Value));
        Assert.Equal(4, run.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrCancellationFlushesAcceptedMetricsBeforeTerminalWrite(bool cancel)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "metric-wait", cancel ? 10 : 2);
        using var operation = await Execution(fixture, catalog).PrepareRunAsync(experiment.Id,
            new Dictionary<string, ParameterValue>(), Token);
        var driver = operation.ExecuteAsync();
        try
        {
            await WaitForAsync(() => File.Exists(Path.Combine(fixture.Workspace, "synthetic-metrics-ready")));
            if (cancel) Assert.True(operation.RequestCancellation());
            await driver.WaitAsync(TimeSpan.FromSeconds(15), Token);
        }
        finally
        {
            if (!driver.IsCompleted) operation.RequestCancellation();
            await driver;
        }
        var run = (await fixture.Store.GetRunAsync(operation.RunId, Token))!;
        Assert.Equal(cancel ? RunStatus.Canceled : RunStatus.TimedOut, run.Status);
        Assert.Equal(130, run.Metrics.Count);
        Assert.Equal(132, run.Version);
    }

    [Fact]
    public async Task OutputOverflowKeepsCombinedOneMiBLimitAndAlreadySavedPoints()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "metric-overflow");
        var id = await Execution(fixture, catalog).StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var run = (await fixture.Store.GetRunAsync(id, Token))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.InRange(run.Metrics.Count, 128, 130);
        Assert.Equal(1024 * 1024, new FileInfo(LogPath(fixture, id, "stdout.log")).Length +
            new FileInfo(LogPath(fixture, id, "stderr.log")).Length);
    }

    [Theory]
    [InlineData("metric-early")]
    [InlineData("metric-ordinary-early")]
    public async Task EarlyOutputWaitsForDurableRunningAcknowledgement(string mode)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TrackingRunner(new ProcessRunner(Files(fixture)), async (started, callback) =>
        {
            await acknowledged.Task;
            await callback(started);
        });
        using var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var driver = operation.ExecuteAsync();
        try
        {
            await WaitForAsync(() => File.Exists(Path.Combine(fixture.Workspace, "synthetic-metrics-ready")));
            var before = (await fixture.Store.GetRunAsync(operation.RunId, Token))!;
            Assert.Equal(RunStatus.Created, before.Status);
            Assert.Empty(before.Metrics);
        }
        finally { acknowledged.TrySetResult(); await driver; runner.Process?.Dispose(); }
        var saved = (await fixture.Store.GetRunAsync(operation.RunId, Token))!;
        Assert.Equal(RunStatus.Succeeded, saved.Status);
        Assert.Equal(2, saved.Metrics.Count);
    }

    [Theory]
    [InlineData("running", RunStatus.Created, 0)]
    [InlineData("metric", RunStatus.Running, 128)]
    [InlineData("metric-swallow", RunStatus.Running, 128)]
    [InlineData("terminal", RunStatus.Running, 2)]
    public async Task EssentialSqliteFailurePropagatesAfterCleanupWithoutFalseSuccess(string stage, RunStatus status, int retained)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, stage.StartsWith("metric", StringComparison.Ordinal) ? "metric-many" : "metric-basic");
        await using (var db = Open(fixture))
        {
            var sql = stage switch
            {
                "metric" or "metric-swallow" => "CREATE TRIGGER reject_metric BEFORE INSERT ON MetricPoints WHEN NEW.Sequence = 129 BEGIN SELECT RAISE(ABORT, 'synthetic SQLite failure'); END",
                "running" => "CREATE TRIGGER reject_run BEFORE UPDATE ON Runs WHEN NEW.Status = 1 BEGIN SELECT RAISE(ABORT, 'synthetic SQLite failure'); END",
                _ => "CREATE TRIGGER reject_run BEFORE UPDATE ON Runs WHEN NEW.Status = 2 BEGIN SELECT RAISE(ABORT, 'synthetic SQLite failure'); END"
            };
            await db.Database.ExecuteSqlRawAsync(sql, Token);
        }
        var runner = new TrackingRunner(new ProcessRunner(Files(fixture)), swallowFailure: stage == "metric-swallow");
        using var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        try
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => operation.ExecuteAsync());
            Assert.Contains("synthetic SQLite failure", error.InnerException!.Message);
            Assert.NotNull(runner.Process);
            Assert.True(runner.Process.HasExited);
            var run = (await fixture.Store.GetRunAsync(operation.RunId, Token))!;
            Assert.Equal(status, run.Status);
            Assert.Equal(retained, run.Metrics.Count);
            Assert.Null(run.FinishedAtUtc);
        }
        finally { runner.Process?.Dispose(); }
    }

    [Fact]
    public async Task CliStartsRealMetricsAndAnotherCliCanReopenSameRunWithoutChangingEvidence()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment) = await SeedAsync(fixture, "metric-invalid");
        var start = await CliAsync(fixture, "yes\n", "run", "start", experiment.Id.Value.ToString());
        Assert.Equal(0, start.Code);
        using var first = JsonDocument.Parse(start.Output);
        var id = new RunId(first.RootElement.GetProperty("id").GetGuid());
        var before = (await fixture.Store.GetRunAsync(id, Token))!;
        var show = await CliAsync(fixture, "", "run", "show", id.Value.ToString(), "--include-logs");
        Assert.Equal(0, show.Code);
        using var second = JsonDocument.Parse(show.Output);
        Assert.Equal(id.Value, second.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", second.RootElement.GetProperty("status").GetString());
        var after = (await new SqliteResearchStore(fixture.Database, readOnly: true).GetRunAsync(id, Token))!;
        Assert.Equal(new[] { 0.5, 0.2 }, after.Metrics.Select(point => point.Value));
        Assert.Equal(before.Metrics.Select(point => point.Id), after.Metrics.Select(point => point.Id));
        Assert.Equal("InvalidFieldType", Assert.Single(await DiagnosticsAsync(fixture, id)).Code);
    }

    [Fact]
    public async Task DiagnosticFileWriteFailureCannotBecomeSucceeded()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "metric-invalid");
        var runner = new ProcessRunner(Files(fixture))
        {
            LogWriter = stream =>
            {
                if (((FileStream)stream).Name.EndsWith("protocol.log", StringComparison.Ordinal)) stream.Dispose();
                return stream;
            }
        };
        var id = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), Token);
        var run = (await fixture.Store.GetRunAsync(id, Token))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Single(run.Metrics);
        Assert.Equal(0.5, run.Metrics[0].Value);
        Assert.DoesNotContain(fixture.Root, run.FailureReason!);
    }
    private static RunLogFiles Files(TestWorkspace fixture) => new(Path.Combine(fixture.Root, "logs"));
    private static string LogPath(TestWorkspace fixture, RunId id, string name) =>
        Path.Combine(fixture.Root, "logs", id.Value.ToString("N"), name);
    private static RunExecution Execution(TestWorkspace fixture, ProjectCatalog catalog) =>
        new(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner(Files(fixture)));
    private static ResearchDbContext Open(TestWorkspace fixture) => new(new DbContextOptionsBuilder<ResearchDbContext>()
        .UseSqlite($"Data Source={fixture.Database};Pooling=False").Options);

    private static async Task<ProtocolDiagnostic[]> DiagnosticsAsync(TestWorkspace fixture, RunId id)
    {
        await using var stream = Files(fixture).OpenRead(id, RunLogStream.Protocol);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(Token);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return new ProtocolDiagnostic(json.RootElement.GetProperty("line").GetInt64(), json.RootElement.GetProperty("code").GetString()!);
        }).ToArray();
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    private static async Task<(ProjectCatalog, Experiment)> SeedAsync(TestWorkspace fixture, string mode, int timeout = 10)
    {
        var executable = OperatingSystem.IsWindows() ? "ResearchLab.ProcessFixture.exe" : "ResearchLab.ProcessFixture";
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ProcessFixture")))
        {
            var destination = Path.Combine(fixture.Workspace, Path.GetFileName(file));
            File.Copy(file, destination);
            if (!OperatingSystem.IsWindows() && Path.GetFileName(file) == executable)
                File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }
        fixture.WriteConfiguration(executable, [mode], timeout);
        await fixture.Store.InitializeAsync(Token);
        var project = new Project(ProjectId.New(), "Synthetic structured project", fixture.Workspace, DateTimeOffset.UnixEpoch);
        await fixture.Store.AddProjectAsync(project, Token);
        var experiment = new Experiment(ExperimentId.New(), project.Id, "Synthetic metrics", "", "benchmark", [], DateTimeOffset.UnixEpoch);
        await fixture.Store.AddExperimentAsync(experiment, Token);
        var catalog = fixture.Catalog();
        var review = await catalog.ReviewTaskAsync(project.Id, "benchmark", Token);
        await catalog.ConfirmTaskAsync(project.Id, "benchmark", review.Fingerprint, Token);
        return (catalog, experiment);
    }

    private sealed class TrackingRunner(IProcessRunner inner,
        Func<ProcessStarted, Func<ProcessStarted, Task>, Task>? acknowledge = null, bool swallowFailure = false) : IProcessRunner
    {
        private readonly bool swallow = swallowFailure;
        public Process? Process { get; private set; }
        public IProcessExecution CreateExecution(RunId id, PreparedRun launch) => new Tracked(inner.CreateExecution(id, launch), this, acknowledge);
        private sealed class Tracked(IProcessExecution inner, TrackingRunner owner,
            Func<ProcessStarted, Func<ProcessStarted, Task>, Task>? acknowledge) : IProcessExecution
        {
            public bool RequestCancellation() => inner.RequestCancellation();
            private async Task StartedAsync(ProcessStarted started, Func<ProcessStarted, Task> callback)
            {
                owner.Process = Process.GetProcessById(started.ProcessId);
                if (acknowledge is null) await callback(started);
                else await acknowledge(started, callback);
            }
            public Task<ProcessResult> ExecuteAsync(Func<ProcessStarted, Task> onStarted) =>
                inner.ExecuteAsync(started => StartedAsync(started, onStarted));
            public async Task<ProcessResult> ExecuteAsync(Func<ProcessStarted, Task> onStarted, IProcessOutputSink output)
            {
                try { return await inner.ExecuteAsync(started => StartedAsync(started, onStarted), output); }
                catch (DbUpdateException) when (owner.swallow) { return new ProcessResult(0, false, null); }
            }
        }
    }

    private static async Task<(int Code, string Output)> CliAsync(TestWorkspace fixture, string input, params string[] words)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var word in new[] { typeof(ConsoleHost).Assembly.Location, "--database", fixture.Database, "--data-dir", Path.Combine(fixture.Root, "logs") }.Concat(words))
            start.ArgumentList.Add(word);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        await process.StandardInput.WriteAsync(input.AsMemory(), Token);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(20), Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(Token); }
        }
        Assert.True(process.ExitCode == 0, await stderr);
        return (process.ExitCode, await stdout);
    }
}
