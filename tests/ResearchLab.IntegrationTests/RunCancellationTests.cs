using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed partial class RunExecutionTests
{
    [Fact]
    public async Task CancelingWaitDoesNotCancelOrAbandonOwnedProcess()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "logged-wait");
        await TrustAsync(catalog, experiment);
        var runner = new ObservingRunner(new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs"))));
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var driver = operation.ExecuteAsync();
        var started = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var process = Process.GetProcessById(started.ProcessId);
        try
        {
            using var wait = new CancellationTokenSource();
            var waiting = operation.WaitAsync(wait.Token);
            wait.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(process.HasExited);
            Assert.False(driver.IsCompleted);
            Assert.All(Enumerable.Range(0, 20), _ => Assert.Same(driver, operation.ExecuteAsync()));
            Assert.Equal(RunStatus.Running, (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!.Status);
            Assert.Empty((await new RunRecovery(fixture.Store).RecoverAsync(TestContext.Current.CancellationToken)).Recovered);
            Assert.Throws<InvalidOperationException>(() => operation.Dispose());
            for (var index = 0; index < 20; index++)
                Assert.True(operation.RequestCancellation());
            Assert.False(process.HasExited); // Request intent is not a terminal fact.
            runner.Acknowledge.TrySetResult();
            await driver.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(process.HasExited);
            await AssertSingleTerminalAsync(fixture, operation, RunStatus.Canceled);
        }
        finally
        {
            runner.Acknowledge.TrySetResult();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await driver;
        }
    }

    [Fact]
    public async Task NaturalExitWinsLateCancellationEvenBeforeStartAcknowledgementReturns()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var runner = new ObservingRunner(new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs"))));
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var driver = operation.ExecuteAsync();
        var started = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var process = Process.GetProcessById(started.ProcessId);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.False(operation.RequestCancellation());
        }
        finally { runner.Acknowledge.TrySetResult(); }
        await driver;
        await AssertSingleTerminalAsync(fixture, operation, RunStatus.Succeeded);
        Assert.False(operation.RequestCancellation());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task CancelAndTimeoutChooseOneCauseAndOneTerminalWrite(bool? cancellationFirst)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait", timeout: 2);
        await TrustAsync(catalog, experiment);
        var clock = new DeadlineClock();
        var runner = new ObservingRunner(new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs")), timeProvider: clock));
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner, clock)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var driver = operation.ExecuteAsync();
        var started = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var process = Process.GetProcessById(started.ProcessId);
        bool accepted;
        try
        {
            if (cancellationFirst == true)
            {
                accepted = operation.RequestCancellation();
                clock.FireDeadline();
            }
            else if (cancellationFirst == false)
            {
                clock.FireDeadline();
                accepted = operation.RequestCancellation();
            }
            else
            {
                using var barrier = new Barrier(2);
                var cancel = Task.Run(() => { barrier.SignalAndWait(); return operation.RequestCancellation(); }, TestContext.Current.CancellationToken);
                var timeout = Task.Run(() => { barrier.SignalAndWait(); clock.FireDeadline(); }, TestContext.Current.CancellationToken);
                await Task.WhenAll(cancel, timeout);
                accepted = await cancel;
            }
            Assert.Equal(RunStatus.Running, (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!.Status);
            runner.Acknowledge.TrySetResult();
            await driver.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(process.HasExited);
            await AssertSingleTerminalAsync(fixture, operation, accepted ? RunStatus.Canceled : RunStatus.TimedOut);
        }
        finally
        {
            runner.Acknowledge.TrySetResult();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await driver;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStopRetainsOwnerAndRunningUntilSameProcessCleanupIsRetried(bool ignoreFirstStop)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait");
        await TrustAsync(catalog, experiment);
        var terminator = new DeniedFirstStop(ignoreFirstStop);
        var runner = new ObservingRunner(new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs")), terminator));
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var driver = operation.ExecuteAsync();
        var started = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var process = Process.GetProcessById(started.ProcessId);
        try
        {
            operation.RequestCancellation();
            runner.Acknowledge.TrySetResult();
            // An ignored stop must hit the bounded exit wait, not hang the foreground driver.
            var error = await Assert.ThrowsAsync<ProcessCleanupException>(() =>
                driver.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            Assert.Same(operation, error.Operation);
            Assert.False(process.HasExited);
            var active = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
            Assert.Equal(RunStatus.Running, active.Status);
            Assert.Null(active.FinishedAtUtc);
            Assert.Equal(1, active.Version);
            Assert.Empty((await new RunRecovery(fixture.Store).RecoverAsync(TestContext.Current.CancellationToken)).Recovered);
            Assert.Throws<InvalidOperationException>(() => operation.Dispose());
            operation.RequestCancellation();
            await error.Operation!.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(process.HasExited);
            Assert.Equal(2, terminator.Calls);
            Assert.Equal(1, runner.StartCount);
            await AssertSingleTerminalAsync(fixture, operation, RunStatus.Canceled);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await driver; } catch (ProcessCleanupException) { await operation.ExecuteAsync(); }
        }
    }

    [Fact]
    public async Task FailedAcknowledgementAndFailedStopRetainCleanupBeforeSurfacingOriginalStorageError()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait");
        await TrustAsync(catalog, experiment);
        await using (var db = Open(fixture))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_running BEFORE UPDATE ON Runs
                WHEN NEW.Status = 1
                BEGIN SELECT RAISE(ABORT, 'Synthetic startup write failure'); END;
                """, TestContext.Current.CancellationToken);
        var terminator = new DeniedFirstStop();
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(),
                new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs")), terminator))
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ProcessCleanupException>(() => operation.ExecuteAsync());
        Assert.Same(operation, error.Operation);
        var saved = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Created, saved.Status);
        try
        {
            var storage = await Assert.ThrowsAsync<DbUpdateException>(() => operation.ExecuteAsync());
            Assert.Contains("Synthetic startup write failure", storage.InnerException!.Message);
            Assert.Equal(2, terminator.Calls);
            Assert.True(terminator.LastProcessExited);
            saved = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
            Assert.Equal(RunStatus.Created, saved.Status);
            Assert.Null(saved.FinishedAtUtc);
        }
        finally
        {
            // Retry the retained handle if an assertion failed before cleanup.
            try { await operation.ExecuteAsync(); } catch (DbUpdateException) { }
        }
    }

    [Fact]
    public async Task CancellationBeforeExecutionNeverStartsAProcess()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait");
        await TrustAsync(catalog, experiment);
        var runner = new ObservingRunner(new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs"))));
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        Assert.True(operation.RequestCancellation());
        await operation.ExecuteAsync();
        var run = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Canceled, run.Status);
        Assert.Null(run.ProcessId);
        Assert.Null(run.StartedAtUtc);
        Assert.Equal(1, run.Version);
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public async Task PreparationDoesNotAuthorizeDelayedLaunchAfterTaskChanges()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new NeverRunner())
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        fixture.WriteConfiguration(ExecutableName, ["wait"]);
        await operation.ExecuteAsync();
        var run = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Null(run.ProcessId);
    }

    private static async Task AssertSingleTerminalAsync(TestWorkspace fixture, RunOperation operation, RunStatus expected)
    {
        var driver = operation.ExecuteAsync();
        var repeated = Enumerable.Range(0, 20).Select(_ => operation.ExecuteAsync()).ToArray();
        Assert.All(repeated, task => Assert.Same(driver, task));
        await Task.WhenAll(repeated);
        var run = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
        Assert.Equal(expected, run.Status);
        Assert.Equal(2, run.Version);
        Assert.NotNull(run.FinishedAtUtc);
        if (expected is RunStatus.Canceled or RunStatus.TimedOut) Assert.Null(run.ExitCode);
        await using var db = Open(fixture);
        Assert.Single(await db.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class ObservingRunner(IProcessRunner inner) : IProcessRunner
    {
        public TaskCompletionSource<ProcessStarted> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledge { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount { get; private set; }
        public IProcessExecution CreateExecution(RunId runId, PreparedRun launch) =>
            new ObservedProcess(this, inner.CreateExecution(runId, launch));

        private sealed class ObservedProcess(ObservingRunner owner, IProcessExecution inner) : IProcessExecution
        {
            public bool RequestCancellation() => inner.RequestCancellation();
            public Task<ProcessResult> ExecuteAsync(Func<ProcessStarted, Task> onStarted) => inner.ExecuteAsync(async started =>
            {
                owner.StartCount++;
                await onStarted(started);
                owner.Started.TrySetResult(started);
                await owner.Acknowledge.Task;
            });
        }
    }

    private sealed class DeniedFirstStop(bool ignoreFirstStop = false) : IProcessTerminator
    {
        public int Calls { get; private set; }
        public bool LastProcessExited { get; private set; }
        public void KillTree(Process process)
        {
            if (++Calls == 1)
            {
                if (ignoreFirstStop) return;
                throw new Win32Exception(5);
            }
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            LastProcessExited = process.HasExited;
        }
    }

    // Manual deadline, not a fake process or invented execution evidence.
    private sealed class DeadlineClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        private ManualDeadline? timer;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new ManualDeadline(callback, state, dueTime);
        public void FireDeadline()
        {
            var current = timer ?? throw new InvalidOperationException("No execution deadline.");
            Interlocked.Add(ref ticks, current.DueTime.Ticks);
            current.Fire();
        }

        private sealed class ManualDeadline(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            public TimeSpan DueTime { get; } = dueTime;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

