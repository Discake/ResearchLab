using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class RunExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorExceptionPersistsFailedWithoutInventingExitCode(bool started)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var runner = new DelegateRunner(async (_, onStarted) =>
        {
            if (started)
                await onStarted(new ProcessStarted(123, DateTimeOffset.UtcNow));
            throw new IOException("Synthetic private path must not be persisted.");
        });
        var id = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(started ? 2 : 1, run.Version);
        Assert.Equal(started, run.StartedAtUtc.HasValue);
        Assert.Null(run.ExitCode);
        Assert.NotEmpty(run.FailureReason!);
        Assert.DoesNotContain("private path", run.FailureReason!);
    }

    [Theory]
    [InlineData("Running", RunStatus.Created, "propagate")]
    [InlineData("Running", RunStatus.Created, "wrap")]
    [InlineData("Running", RunStatus.Created, "swallow")]
    [InlineData("Succeeded", RunStatus.Running, "propagate")]
    public async Task SqliteWriteFailurePropagatesWithoutBeingReclassified(string rejectedStatus, RunStatus retainedStatus, string callbackError)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait", timeout: 1);
        await TrustAsync(catalog, experiment);
        var sql = rejectedStatus == "Running" ? """
                CREATE TRIGGER reject_state BEFORE UPDATE ON Runs
                WHEN NEW.Status = 1
                BEGIN SELECT RAISE(ABORT, 'Synthetic SQLite failure'); END;
                """ : """
                CREATE TRIGGER reject_state BEFORE UPDATE ON Runs
                WHEN NEW.Status = 2
                BEGIN SELECT RAISE(ABORT, 'Synthetic SQLite failure'); END;
                """;
        await using (var db = Open(fixture))
            await db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
        // A real process exercises cleanup when saving Running throws.
        Process? owned = null;
        var runner = new DelegateRunner(async (launch, onStarted) =>
        {
            try
            {
                return await new ProcessRunner().ExecuteAsync(launch, async started =>
                {
                    owned = Process.GetProcessById(started.ProcessId);
                    await onStarted(started);
                });
            }
            catch (DbUpdateException) when (callbackError == "wrap")
            { throw new IOException("Synthetic cleanup failure masking a SQLite error."); }
            catch (DbUpdateException) when (callbackError == "swallow")
            { return new ProcessResult(0, false, null); }
        });
        // Use success for the terminal-write case without replacing the trusted launch.
        if (rejectedStatus == "Succeeded")
        {
            fixture.WriteConfiguration(ExecutableName, ["success"]);
            await TrustAsync(catalog, experiment);
        }
        try
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
                    .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken));
            Assert.Contains("Synthetic SQLite failure", error.InnerException!.Message);
            Assert.NotNull(owned);
            Assert.True(owned.HasExited);
            await using var check = Open(fixture);
            Assert.Equal(retainedStatus, (await check.Set<Run>().SingleAsync(TestContext.Current.CancellationToken)).Status);
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task CompletedProcessDoesNotTimeOutWhileStartupAcknowledgementIsDelayed()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success", timeout: 2);
        await TrustAsync(catalog, experiment);
        var prepared = await new RunPreparation(catalog).PrepareAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var result = await new ProcessRunner().ExecuteAsync(prepared, async started =>
        {
            using var owned = Process.GetProcessById(started.ProcessId);
            await owned.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        });
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task FailureToReadExecutableDuringFinalTrustCheckPersistsFailed()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        FileStream? locked = null;
        var inspector = new ChangingInspector(() =>
        {
            locked = new FileStream(Path.Combine(fixture.Workspace, ExecutableName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Task.CompletedTask;
        });
        try
        {
            var id = await new RunExecution(fixture.Store, catalog, inspector, new NeverRunner())
                .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
            var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Null(run.ProcessId);
            Assert.DoesNotContain(fixture.Workspace, run.FailureReason!);
        }
        finally { locked?.Dispose(); }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, "Synthetic result collection failure.")]
    public async Task IncompleteResultCannotProduceSucceeded(int? exitCode, string? reason)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var runner = new DelegateRunner(async (_, onStarted) =>
        {
            await onStarted(new ProcessStarted(123, DateTimeOffset.UtcNow));
            return new ProcessResult(exitCode, false, reason);
        });
        var id = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        Assert.Equal(RunStatus.Failed, (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task TimeoutStopsLiveProcessTreeAndDrainsInheritedPipes()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "tree", timeout: 3);
        await TrustAsync(catalog, experiment);
        Process? root = null;
        Process? child = null;
        var runner = new DelegateRunner((launch, onStarted) => new ProcessRunner().ExecuteAsync(launch, async started =>
        {
            root = Process.GetProcessById(started.ProcessId);
            await onStarted(started);
            var pidFile = Path.Combine(fixture.Workspace, "synthetic-child.pid");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!File.Exists(pidFile))
                await Task.Delay(10, deadline.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, deadline.Token), System.Globalization.CultureInfo.InvariantCulture));
        }));
        try
        {
            var id = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
                .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(RunStatus.TimedOut, (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status);
            Assert.NotNull(root);
            Assert.True(root.HasExited);
            Assert.NotNull(child);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            // Clean up only process handles obtained from this synthetic fixture.
            if (root is not null && !root.HasExited)
                root.Kill(entireProcessTree: true);
            if (child is not null && !child.HasExited)
                child.Kill(entireProcessTree: true);
            root?.Dispose();
            child?.Dispose();
        }
    }

    [Theory]
    [InlineData("success", RunStatus.Succeeded, 0)]
    [InlineData("fail", RunStatus.Failed, 7)]
    [InlineData("streams", RunStatus.Succeeded, 0)]
    [InlineData("parameters", RunStatus.Succeeded, 0)]
    [InlineData("literal", RunStatus.Succeeded, 0)]
    public async Task ExecutesTrustedProgramAndPersistsOutcome(string mode, RunStatus status, int exitCode)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode);
        await TrustAsync(catalog, experiment);
        var runner = new InspectingRunner(fixture, experiment.Id);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner);
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(status, run.Status);
        Assert.Equal(exitCode, run.ExitCode);
        Assert.Equal(2, run.Version);
        Assert.True(run.ProcessId > 0);
        Assert.NotNull(run.StartedAtUtc);
        Assert.True(run.FinishedAtUtc >= run.StartedAtUtc);
        Assert.Equal(ParameterValue.From(2), run.ParametersSnapshot["n"]);
        Assert.Empty(run.Metrics);
        Assert.Equal(status == RunStatus.Failed, run.FailureReason is not null);
        Assert.Equal(RunStatus.Running, runner.StatusAfterStart);
    }

    [Fact]
    public async Task MissingTrustOrInvalidParametersCannotReachExecutor()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new NeverRunner());
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() => service.StartRunAsync(
            experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
        await TrustAsync(catalog, experiment);
        Assert.Equal("UnknownParameter", (await Assert.ThrowsAsync<DomainException>(() => service.StartRunAsync(
            experiment.Id, new Dictionary<string, ParameterValue> { ["other"] = ParameterValue.From(1) }, TestContext.Current.CancellationToken))).Code);
        await using var db = Open(fixture);
        Assert.Empty(await db.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidExecutableFailsWithoutInventingRunningOrExitCode()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        fixture.WriteConfiguration("broken.exe");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "broken.exe"), "This is not an executable image.", TestContext.Current.CancellationToken);
        await TrustAsync(catalog, experiment);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(1, run.Version);
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.ProcessId);
        Assert.Null(run.ExitCode);
        Assert.NotEmpty(run.FailureReason!);
    }

    [Fact]
    public async Task WindowsBatchFileCannotFallBackToCommandProcessor()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        fixture.WriteConfiguration("task.cmd");
        await TrustAsync(catalog, experiment);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Null(run.ProcessId);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
    }

    [Theory]
    [InlineData("wait", RunStatus.TimedOut)]
    [InlineData("overflow", RunStatus.Failed)]
    public async Task BoundsExecutionTimeAndOutput(string mode, RunStatus expected)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode, timeout: mode == "wait" ? 1 : 10);
        await TrustAsync(catalog, experiment);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(expected, run.Status);
        Assert.NotEmpty(run.FailureReason!);
        if (expected == RunStatus.TimedOut)
            Assert.Null(run.ExitCode);
    }

    [Fact]
    public async Task ChangedBinaryAfterPreparationBlocksHandoff()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var inspector = new ChangingInspector(async () =>
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspace, ExecutableName), "changed", TestContext.Current.CancellationToken));
        var service = new RunExecution(fixture.Store, catalog, inspector, new NeverRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("TaskNotTrusted", run.FailureReason!);
        Assert.Null(run.ProcessId);
    }

    [Fact]
    public async Task FailingStartupAcknowledgementStopsOwnedProcessAndPropagatesError()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait");
        await TrustAsync(catalog, experiment);
        var prepared = await new RunPreparation(catalog).PrepareAsync(experiment.Id,
            new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        Process? owned = null;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProcessRunner().ExecuteAsync(prepared, started =>
            {
                owned = Process.GetProcessById(started.ProcessId);
                throw new InvalidOperationException("Synthetic persistence failure.");
            }));
            Assert.NotNull(owned);
            Assert.True(owned.HasExited);
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task ChangedExperimentCannotReplacePreparedDefinition()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var inspector = new ChangingInspector(async () =>
        {
            await using var db = Open(fixture);
            var stored = await db.Set<Experiment>().SingleAsync(TestContext.Current.CancellationToken);
            stored.Revise(new ExperimentDefinition("Changed", "", "benchmark", []), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        var service = new RunExecution(fixture.Store, catalog, inspector, new NeverRunner());
        Assert.Equal("PreparationChanged", (await Assert.ThrowsAsync<CatalogException>(() => service.StartRunAsync(
            experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
        await using var check = Open(fixture);
        Assert.Empty(await check.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewConsentForChangedTaskDoesNotAuthorizeOldPreparedLaunch()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var inspector = new ChangingInspector(async () =>
        {
            fixture.WriteConfiguration(ExecutableName, ["fail"]);
            // Simulates a separate explicit owner review/approval during preparation.
            await TrustAsync(catalog, experiment);
        });
        var service = new RunExecution(fixture.Store, catalog, inspector, new NeverRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("PreparationChanged", run.FailureReason!);
        Assert.Equal(new[] { "success" }, run.ExperimentSnapshot.Task.Arguments);
        Assert.Null(run.ProcessId);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "ResearchLab.ProcessFixture.exe" : "ResearchLab.ProcessFixture";

    private static async Task<(ProjectCatalog, Experiment)> SeedAsync(TestWorkspace fixture, string mode, int timeout = 10)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ProcessFixture")))
        {
            var destination = Path.Combine(fixture.Workspace, Path.GetFileName(file));
            File.Copy(file, destination);
            if (!OperatingSystem.IsWindows() && Path.GetFileName(file) == ExecutableName)
                File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }
        var args = mode == "literal" ? new[] { mode, "a b", "$(command)", "; echo forbidden", "%PATH%" } : [mode];
        fixture.WriteConfiguration(ExecutableName, args, timeout);
        var store = fixture.Store;
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var project = new Project(ProjectId.New(), "Synthetic execution project", fixture.Workspace, DateTimeOffset.UnixEpoch);
        await store.AddProjectAsync(project, TestContext.Current.CancellationToken);
        var experiment = new Experiment(ExperimentId.New(), project.Id, "Synthetic execution", "", "benchmark",
            [new ParameterDefinition("n", ParameterType.Int, defaultValue: ParameterValue.From(2))], DateTimeOffset.UnixEpoch);
        await store.AddExperimentAsync(experiment, TestContext.Current.CancellationToken);
        return (fixture.Catalog(), experiment);
    }

    private static async Task TrustAsync(ProjectCatalog catalog, Experiment experiment)
    {
        var review = await catalog.ReviewTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, review.Fingerprint, TestContext.Current.CancellationToken);
    }

    private static ResearchDbContext Open(TestWorkspace fixture) => new(new DbContextOptionsBuilder<ResearchDbContext>()
        .UseSqlite($"Data Source={fixture.Database};Pooling=False").Options);

    private sealed class NeverRunner : IProcessRunner
    {
        public Task<ProcessResult> ExecuteAsync(PreparedRun launch, Func<ProcessStarted, Task> onStarted) =>
            throw new InvalidOperationException("Executor must not be reached.");
    }

    private sealed class DelegateRunner(Func<PreparedRun, Func<ProcessStarted, Task>, Task<ProcessResult>> execute) : IProcessRunner
    {
        public Task<ProcessResult> ExecuteAsync(PreparedRun launch, Func<ProcessStarted, Task> onStarted) => execute(launch, onStarted);
    }

    private sealed class ChangingInspector(Func<Task> change) : IWorkspaceInspector
    {
        public async Task<WorkspaceInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            var result = await new WorkspaceInspector().InspectAsync(path, cancellationToken);
            await change();
            return result;
        }

        public Task<TaskReview> ReviewTaskAsync(Project project, string taskKey, CancellationToken cancellationToken = default) =>
            new WorkspaceInspector().ReviewTaskAsync(project, taskKey, cancellationToken);
    }

    private sealed class InspectingRunner(TestWorkspace fixture, ExperimentId experimentId) : IProcessRunner
    {
        public RunStatus? StatusAfterStart { get; private set; }

        public async Task<ProcessResult> ExecuteAsync(PreparedRun launch, Func<ProcessStarted, Task> onStarted)
        {
            await using var db = Open(fixture);
            var created = Assert.Single(await db.Set<Run>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(RunStatus.Created, created.Status);
            Assert.Null(created.ProcessId);
            return await new ProcessRunner().ExecuteAsync(launch, async started =>
            {
                await onStarted(started);
                var runs = await db.Set<Run>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
                StatusAfterStart = Assert.Single(runs).Status;
                Assert.Equal(experimentId, runs[0].ExperimentId);
            });
        }
    }
}
