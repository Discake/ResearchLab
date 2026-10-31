using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using Xunit;
using static ResearchLab.Execution.WindowsNativeMethods;

namespace ResearchLab.IntegrationTests;

public sealed partial class RunExecutionTests
{
    [WindowsFact]
    public async Task WindowsGroupClosedJobRetainsUnconfirmedOwnerInsteadOfInventingSuccess()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment) = await SeedAsync(fixture, "success");
        using var owner = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", ExecutableName))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "closed-job-owner", fixture.Database, experiment.Id.Value.ToString(), Path.Combine(fixture.Root, "logs") }
        })!;
        var output = owner.StandardOutput.ReadToEndAsync();
        var error = owner.StandardError.ReadToEndAsync();
        try
        {
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(owner.ExitCode == 0, await error.WaitAsync(TimeSpan.FromSeconds(5)));
            using var result = JsonDocument.Parse(await output.WaitAsync(TimeSpan.FromSeconds(5)));
            var run = (await fixture.Store.GetRunAsync(new RunId(result.RootElement.GetProperty("Id").GetGuid())))!;
            Assert.Equal(RunStatus.Running, run.Status);
            Assert.Equal(1, run.Version);
            Assert.Null(run.FinishedAtUtc);
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(true); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    [WindowsFact]
    public async Task WindowsGroupNativeRunnerPreservesUnicodeJsonArgumentsAndOutputDirectory()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        string[] arguments = ["native-echo", "0", "", "Юникод 世界", "{\"text\":\"a b\\\\\"}", "a\"b", "a b\\"];
        fixture.WriteConfiguration(ExecutableName, arguments);
        // A persisted definition with a Unicode/JSON string exercises the full parameter path.
        var text = "Привет 世界 {\"n\":2} 😀";
        await using (var db = Open(fixture))
        {
            var stored = await db.Set<Experiment>().SingleAsync();
            stored.Revise(new ExperimentDefinition("Synthetic Unicode", "", "benchmark",
                [new ParameterDefinition("text", ParameterType.String, defaultValue: ParameterValue.From(text))]), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        await TrustAsync(catalog, experiment);
        var logs = new RunLogFiles(Path.Combine(fixture.Root, "logs"));
        var id = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner(logs))
            .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>()).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(RunStatus.Succeeded, (await fixture.Store.GetRunAsync(id))!.Status);
        using var reader = new StreamReader(logs.OpenRead(id, RunLogStream.Stdout));
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        var root = json.RootElement;
        Assert.Equal(arguments.Skip(2), root.GetProperty("Arguments").EnumerateArray().Select(item => item.GetString()));
        using var parameters = JsonDocument.Parse(root.GetProperty("Parameters").GetString()!);
        Assert.Equal(text, parameters.RootElement.GetProperty("text").GetString());
        Assert.Equal(fixture.Workspace, root.GetProperty("Cwd").GetString());
        Assert.Equal(Path.Combine(fixture.Root, "logs", id.Value.ToString("N"), "output"), root.GetProperty("Output").GetString());
    }

    [WindowsFact]
    public async Task WindowsGroupWaitsForDescendantEvenAfterRootAndPipesExit()
    {
        foreach (var pipes in new[] { "held", "closed" })
        {
            using var fixture = new TestWorkspace();
            await using var group = await StartGroupAsync(fixture, pipes);
            Assert.False(group.Driver.IsCompleted);
            Assert.False(group.Child.HasExited);
            Assert.Equal(RunStatus.Running, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
            await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "group-release"), "release");
            await group.FinishAsync();
            var run = (await fixture.Store.GetRunAsync(group.Operation.RunId))!;
            Assert.Equal(RunStatus.Succeeded, run.Status);
            Assert.False(group.Operation.RequestCancellation()); // The confirmed empty group wins late intent.
            Assert.Equal(0, run.ExitCode);
            Assert.Equal(2, run.Version);
            Assert.Equal(1, group.Observer.StartCount);
        }
    }

    [WindowsFact]
    public async Task WindowsGroupTimeoutStillAppliesAfterRootExit()
    {
        using var fixture = new TestWorkspace();
        var clock = new DeadlineClock();
        await using var group = await StartGroupAsync(fixture, clock: clock);
        Assert.False(group.Child.HasExited);
        clock.FireDeadline();
        await group.FinishAsync();
        Assert.Equal(RunStatus.TimedOut, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
    }

    [WindowsFact]
    public async Task WindowsGroupLateCancellationStopsDescendantHoldingBothPipes()
    {
        using var fixture = new TestWorkspace();
        await using var group = await StartGroupAsync(fixture);
        Assert.All(Enumerable.Range(0, 12), _ => Assert.True(group.Operation.RequestCancellation()));
        await group.FinishAsync();
        Assert.Equal(RunStatus.Canceled, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
        var directory = Path.Combine(fixture.Root, "logs", group.Operation.RunId.Value.ToString("N"));
        Assert.Contains("synthetic child stdout", await File.ReadAllTextAsync(Path.Combine(directory, "stdout.log")));
        Assert.Contains("synthetic child stderr", await File.ReadAllTextAsync(Path.Combine(directory, "stderr.log")));
    }

    [WindowsFact]
    public async Task WindowsGroupQueryFailureCannotSucceedAndRetryNeverLaunchesAgain()
    {
        using var fixture = new TestWorkspace();
        await using var group = await StartGroupAsync(fixture);
        group.Jobs.DenyQuery = true;
        var error = await Assert.ThrowsAsync<ProcessCleanupException>(() => group.Driver.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.Same(group.Operation, error.Operation);
        Assert.True(group.Jobs.Terminations > 0);
        Assert.Equal(RunStatus.Running, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
        Assert.Throws<InvalidOperationException>(() => group.Operation.Dispose());
        group.Jobs.DenyQuery = false;
        group.Driver = group.Operation.ExecuteAsync();
        await group.FinishAsync();
        Assert.Equal(1, group.Observer.StartCount);
        var run = (await fixture.Store.GetRunAsync(group.Operation.RunId))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("group completion", run.FailureReason!);
    }

    [WindowsFact]
    public async Task WindowsGroupTerminateFailureRetainsLiveGroupUntilCleanupRetry()
    {
        using var fixture = new TestWorkspace();
        await using var group = await StartGroupAsync(fixture);
        group.Jobs.DenyTermination = true;
        Assert.True(group.Operation.RequestCancellation());
        await Assert.ThrowsAsync<ProcessCleanupException>(() => group.Driver.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.False(group.Child.HasExited);
        Assert.Equal(RunStatus.Running, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
        group.Jobs.DenyTermination = false;
        group.Driver = group.Operation.ExecuteAsync();
        await group.FinishAsync();
        Assert.Equal(RunStatus.Canceled, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
        Assert.Equal(1, group.Observer.StartCount);
        Assert.Equal(2, group.Jobs.Terminations);
    }

    [WindowsFact]
    public async Task WindowsGroupConcurrentRunsKeepIndependentControlAndLogs()
    {
        using var firstFixture = new TestWorkspace();
        using var secondFixture = new TestWorkspace();
        var launches = await Task.WhenAll(StartGroupAsync(firstFixture), StartGroupAsync(secondFixture));
        await using var first = launches[0];
        await using var second = launches[1];
        first.Operation.RequestCancellation();
        await first.FinishAsync();
        Assert.False(second.Child.HasExited);
        Assert.False(second.Driver.IsCompleted);
        await File.WriteAllTextAsync(Path.Combine(secondFixture.Workspace, "group-release"), "release");
        await second.FinishAsync();
        Assert.Equal(RunStatus.Canceled, (await firstFixture.Store.GetRunAsync(first.Operation.RunId))!.Status);
        Assert.Equal(RunStatus.Succeeded, (await secondFixture.Store.GetRunAsync(second.Operation.RunId))!.Status);
    }

    [WindowsFact]
    public async Task WindowsGroupLogWriteFailureStopsWholeGroup()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "tree");
        await TrustAsync(catalog, experiment);
        var jobs = new FaultingJobControl();
        var runner = new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs")))
        {
            JobControl = jobs,
            // A real closed FileStream provokes an actual write failure at the log boundary.
            LogWriter = stream => { stream.Dispose(); return stream; }
        };
        // The tree fixture itself writes no output; use a child that logs and waits instead.
        fixture.WriteConfiguration(ExecutableName, ["group-root", "held"]);
        await TrustAsync(catalog, experiment);
        var id = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>()).WaitAsync(TimeSpan.FromSeconds(15));
        var run = (await fixture.Store.GetRunAsync(id))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("output could not be read or saved", run.FailureReason!);
        Assert.True(jobs.Terminations > 0);
    }

    [WindowsFact]
    public async Task WindowsGroupOutputOverflowAfterRootExitStopsChildAndRetainsByteCap()
    {
        using var fixture = new TestWorkspace();
        await using var group = await StartGroupAsync(fixture, "overflow");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "group-release"), "release");
        await group.FinishAsync();
        var run = (await fixture.Store.GetRunAsync(group.Operation.RunId))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("1 MiB", run.FailureReason!);
        var directory = Path.Combine(fixture.Root, "logs", run.Id.Value.ToString("N"));
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(directory, "stdout.log")).Length +
            new FileInfo(Path.Combine(directory, "stderr.log")).Length);
    }

    [WindowsFact]
    public async Task WindowsGroupTerminalPersistenceFailurePropagatesAfterConfirmedDrain()
    {
        using var fixture = new TestWorkspace();
        await using var group = await StartGroupAsync(fixture);
        await using (var db = Open(fixture))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_group_success BEFORE UPDATE ON Runs
                WHEN NEW.Status = 2 BEGIN SELECT RAISE(ABORT, 'Synthetic group terminal failure'); END;
                """);
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "group-release"), "release");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => group.FinishAsync());
        Assert.Contains("Synthetic group terminal failure", error.InnerException!.Message);
        Assert.Equal(RunStatus.Running, (await fixture.Store.GetRunAsync(group.Operation.RunId))!.Status);
        Assert.Same(group.Driver, group.Operation.ExecuteAsync());
        Assert.Equal(1, group.Observer.StartCount);
    }

    [WindowsFact]
    public async Task WindowsGroupStartupPersistenceFailureStopsAlreadyOrphanedDescendant()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        fixture.WriteConfiguration(ExecutableName, ["group-root", "held"]);
        await TrustAsync(catalog, experiment);
        await using (var db = Open(fixture))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_group_running BEFORE UPDATE ON Runs
                WHEN NEW.Status = 1 BEGIN SELECT RAISE(ABORT, 'Synthetic group startup failure'); END;
                """);
        Process? child = null;
        var jobs = new FaultingJobControl();
        var nativeRunner = new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs"))) { JobControl = jobs };
        var wrapper = new DelegateRunner((launch, onStarted) => nativeRunner.ExecuteAsync(RunId.New(), launch, async started =>
        {
            using var root = Process.GetProcessById(started.ProcessId);
            child = Process.GetProcessById(await ReadChildPidAsync(fixture));
            await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "group-root-exit"), "exit");
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // Actual SQLite Running write now fails with the root already dead and child alive.
            await onStarted(started);
        }));
        try
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), wrapper)
                    .StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>()).WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Contains("Synthetic group startup failure", exception.InnerException!.Message);
            Assert.NotNull(child);
            Assert.True(child.HasExited);
            Assert.True(jobs.Terminations > 0);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                child.Dispose();
            }
        }
    }

    private static async Task<GroupRun> StartGroupAsync(TestWorkspace fixture, string mode = "held", DeadlineClock? clock = null)
    {
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        fixture.WriteConfiguration(ExecutableName, ["group-root", mode], timeout: 30);
        await TrustAsync(catalog, experiment);
        var jobs = new FaultingJobControl();
        var observer = new ObservingRunner(new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs")), timeProvider: clock)
        { JobControl = jobs });
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), observer, clock)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>());
        var driver = operation.ExecuteAsync();
        Process? root = null;
        Process? child = null;
        try
        {
            if (await Task.WhenAny(observer.Started.Task, driver) == driver) await driver;
            var started = await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            root = Process.GetProcessById(started.ProcessId);
            child = Process.GetProcessById(await ReadChildPidAsync(fixture));
            observer.Acknowledge.TrySetResult();
            await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "group-root-exit"), "exit");
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            root.Dispose();
            return new GroupRun(operation, driver, child, observer, jobs);
        }
        catch
        {
            observer.Acknowledge.TrySetResult();
            operation.RequestCancellation();
            root?.Dispose();
            child?.Dispose();
            await driver.WaitAsync(TimeSpan.FromSeconds(15));
            throw;
        }
    }

    private static async Task<int> ReadChildPidAsync(TestWorkspace fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = Path.Combine(fixture.Workspace, "group-child.pid");
        while (true)
        {
            try { if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, timeout.Token), out var pid)) return pid; }
            catch (IOException) { }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class GroupRun(RunOperation operation, Task<RunId> driver, Process child,
        ObservingRunner observer, FaultingJobControl jobs) : IAsyncDisposable
    {
        public RunOperation Operation => operation;
        public Task<RunId> Driver { get; set; } = driver;
        public Process Child => child;
        public ObservingRunner Observer => observer;
        public FaultingJobControl Jobs => jobs;
        private bool childDisposed;
        public async Task FinishAsync()
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(child.HasExited);
            child.Dispose();
            childDisposed = true;
            await Driver.WaitAsync(TimeSpan.FromSeconds(15));
        }
        public async ValueTask DisposeAsync()
        {
            jobs.DenyQuery = jobs.DenyTermination = false;
            if (!childDisposed) child.Dispose(); // Release test-only references before confirming accounting.
            if (!Driver.IsCompleted) operation.RequestCancellation();
            try { await Driver.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (ProcessCleanupException) { await operation.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (DbUpdateException) { } // Expected persistence-failure cases have already confirmed cleanup.
            operation.Dispose();
        }
    }

    private sealed class FaultingJobControl : IWindowsJobControl
    {
        private readonly WindowsJobControl real = new();
        internal volatile bool DenyQuery;
        internal volatile bool DenyTermination;
        internal int Terminations;
        public uint ActiveProcesses(KernelHandle job)
        {
            if (!DenyQuery) return real.ActiveProcesses(job);
            // Real Win32 invalid-buffer failure with a valid Job handle.
            WindowsNativeProcessLauncher.Check(QueryInformationJobObject(job, 1, out _, 0, 0));
            throw new InvalidOperationException("Invalid accounting buffer unexpectedly accepted.");
        }
        public void Terminate(KernelHandle job)
        {
            Interlocked.Increment(ref Terminations);
            if (DenyTermination)
            {
                using var invalid = new KernelHandle();
                real.Terminate(invalid); // Real ERROR_INVALID_HANDLE; the owned group remains untouched.
            }
            else real.Terminate(job);
        }
    }
}
