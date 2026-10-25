using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) Skip = "Requires actual Windows Job containment and named events.";
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCrashSafetyTests
{
    [WindowsTheory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    public async Task ProductionCliDeathKillsObservedGroupWithoutManagedCleanup(int children, bool isolated, bool rootExitsFirst)
    {
        await using var test = await WindowsCrashCase.CreateAsync(children, isolated);
        Assert.Null(typeof(RunExecution).Assembly.GetType("ResearchLab.Application.CrashTestCheckpoint"));
        await test.StartAsync(start =>
        {
            // Shipping binaries must ignore these variables; no checkpoint API is compiled in.
            start.Environment["RESEARCHLAB_CRASH_CHECKPOINT"] = "after-running";
            start.Environment["RESEARCHLAB_CRASH_PIPE"] = "must-not-connect-" + test.Token;
        });
        await test.ReadyAsync();
        await test.SavedStatusAsync(RunStatus.Running);
        await test.LogsRecordedAsync();
        if (children > 0)
        {
            Assert.Equal(!isolated, (await test.ReadLogAsync(false)).Contains("child stdout " + test.Token, StringComparison.Ordinal));
            Assert.Equal(!isolated, (await test.ReadLogAsync(true)).Contains("child stderr " + test.Token, StringComparison.Ordinal));
        }
        var snapshots = await test.SnapshotsAsync();
        if (rootExitsFirst)
        {
            test.Signal("RootExit");
            await test.Root!.WaitForExitAsync().WaitAsync(WindowsCrashCase.Limit);
            Assert.All(test.Children, child => Assert.False(child.HasExited));
            Assert.Equal(RunStatus.Running, (await test.Store.GetRunAsync(test.Run.Id))!.Status);
        }
        await test.KillCliAsync(); // TerminateProcess on CLI only. No tree kill in the assertion path.
        await test.AssertGroupExitedAsync(rootExitsFirst);
        Assert.Equal(RunStatus.Running, (await test.Store.GetRunAsync(test.Run.Id))!.Status);
        Assert.DoesNotContain("Succeeded", await test.Stdout!);
        await AssertRecoveredAsync(test, snapshots);
    }

    [WindowsTheory]
    [InlineData("before-job", false, RunStatus.Created)]
    [InlineData("job-configured", false, RunStatus.Created)]
    [InlineData("native-created", true, RunStatus.Created)]
    [InlineData("before-running", true, RunStatus.Created)]
    [InlineData("after-running", true, RunStatus.Running)]
    public async Task StartupCheckpointCrashPreservesEvidenceAndLeavesNoLiveGroup(string stage, bool created, RunStatus status)
    {
        await using var test = await WindowsCrashCase.CreateAsync(children: stage == "native-created" ? 0 : 2, checkpointStage: stage);
        await test.StartAsync();
        using var checkpoint = await test.AtCheckpointAsync();
        if (created && stage != "native-created") await test.ReadyAsync();
        if (stage == "native-created")
        {
            // Native PID/handle is the barrier here, before launch has closed temporary stdio ends.
            // Managed fixture readiness is a later boundary and must not be required by this pause.
            Assert.NotNull(test.Root);
            Assert.False(test.Root.HasExited);
        }
        Assert.Equal(status, test.Run.Status);
        Assert.Equal(status == RunStatus.Running, test.Run.ProcessId.HasValue);
        var snapshots = await test.SnapshotsAsync();
        await test.KillCliAsync();
        if (created) await test.AssertGroupExitedAsync();
        else Assert.False(File.Exists(Path.Combine(test.OutputDirectory, "crash-ready.json")));
        Assert.False(File.Exists(test.FinallyFile));
        Assert.Equal(status, (await test.Store.GetRunAsync(test.Run.Id))!.Status);
        await AssertRecoveredAsync(test, snapshots);
    }

    [WindowsTheory]
    [InlineData("before-terminal", RunStatus.Running)]
    [InlineData("after-terminal", RunStatus.Succeeded)]
    public async Task TerminalCrashDistinguishesUnsavedResultFromDurableSuccess(string stage, RunStatus durable)
    {
        await using var test = await WindowsCrashCase.CreateAsync(checkpointStage: stage);
        await test.StartAsync();
        await test.ReadyAsync();
        test.Signal("Release"); test.Signal("RootExit");
        using var checkpoint = await test.AtCheckpointAsync();
        await Task.WhenAll(test.Children.Prepend(test.Root!).Select(p => p.WaitForExitAsync())).WaitAsync(WindowsCrashCase.Limit);
        Assert.Equal(durable, test.Run.Status);
        var snapshots = await test.SnapshotsAsync();
        await test.KillCliAsync();
        Assert.False(File.Exists(test.FinallyFile));
        if (durable == RunStatus.Running) await AssertRecoveredAsync(test, snapshots);
        else
        {
            var recovered = await test.RecoverAsync(expectRecovered: false);
            Assert.Equal(RunStatus.Succeeded, recovered.Status);
            Assert.Equal(test.Run.Version, recovered.Version);
            Assert.Equal(0, recovered.ExitCode);
            Assert.Equal(snapshots, await test.SnapshotsAsync());
        }
    }

    [WindowsFact]
    public async Task TwoConcurrentCliOwnersShareDatabaseButNotJobsLogsOutputsOrRecovery()
    {
        await using var first = await WindowsCrashCase.CreateAsync();
        await using var second = await WindowsCrashCase.CreateAsync(sharedStore: first);
        await Task.WhenAll(first.StartAsync(), second.StartAsync());
        await Task.WhenAll(first.ReadyAsync(), second.ReadyAsync());
        await Task.WhenAll(first.SavedStatusAsync(RunStatus.Running), second.SavedStatusAsync(RunStatus.Running));
        await Task.WhenAll(first.LogsRecordedAsync(), second.LogsRecordedAsync());
        var snapshots = await first.SnapshotsAsync();
        var secondSnapshots = await second.SnapshotsAsync();
        Assert.NotEqual(first.OutputDirectory, second.OutputDirectory);
        await first.KillCliAsync();
        await first.AssertGroupExitedAsync();
        await AssertRecoveredAsync(first, snapshots);
        Assert.False(second.Cli.HasExited);
        Assert.False(second.Root!.HasExited);
        Assert.All(second.Children, child => Assert.False(child.HasExited));
        var active = (await second.Store.GetRunAsync(second.Run.Id))!;
        Assert.Equal(RunStatus.Running, active.Status);
        Assert.Equal(1, active.Version);
        second.Signal("Release"); second.Signal("RootExit");
        await second.Cli.WaitForExitAsync().WaitAsync(WindowsCrashCase.Limit);
        Assert.Equal(0, second.Cli.ExitCode);
        Assert.Equal(RunStatus.Succeeded, (await second.Store.GetRunAsync(second.Run.Id))!.Status);
        Assert.Equal(secondSnapshots, await second.SnapshotsAsync());
        foreach (var stream in new[] { "stdout.log", "stderr.log" })
        {
            var firstLog = await File.ReadAllTextAsync(Path.Combine(first.Logs, first.Run.Id.Value.ToString("N"), stream));
            var secondLog = await File.ReadAllTextAsync(Path.Combine(second.Logs, second.Run.Id.Value.ToString("N"), stream));
            Assert.Contains(first.Token, firstLog); Assert.DoesNotContain(second.Token, firstLog);
            Assert.Contains(second.Token, secondLog); Assert.DoesNotContain(first.Token, secondLog);
        }
    }

    [WindowsFact]
    public async Task CorruptOwnerEvidenceAfterRealCrashRemainsUnverified()
    {
        await using var test = await WindowsCrashCase.CreateAsync();
        await test.StartAsync(); await test.ReadyAsync(); await test.SavedStatusAsync(RunStatus.Running);
        await test.KillCliAsync(); await test.AssertGroupExitedAsync();
        var session = Assert.Single(await test.Store.ListIncompleteSessionsAsync());
        var path = Path.Combine(test.Database + ".sessions", session.ToString("N") + ".session");
        var original = await File.ReadAllBytesAsync(path);
        try
        {
            await File.WriteAllTextAsync(path, "Synthetic corrupt ownership evidence");
            var result = await WindowsCrashCase.RunCliAsync(test.Database, test.Logs, "run", "recover");
            Assert.Equal(0, result.Code);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Empty(json.RootElement.GetProperty("recovered").EnumerateArray());
            Assert.Contains(session, json.RootElement.GetProperty("unverifiedSessions").EnumerateArray().Select(value => value.GetGuid()));
            var saved = (await test.Store.GetRunAsync(test.Run.Id))!;
            Assert.Equal(RunStatus.Running, saved.Status);
            Assert.Equal(1, saved.Version);
        }
        finally { await File.WriteAllBytesAsync(path, original); }
        Assert.Equal(RunStatus.Interrupted, (await test.RecoverAsync()).Status);
    }

    [WindowsTheory]
    [InlineData("before-running", 1, RunStatus.Created)]
    [InlineData("before-terminal", 2, RunStatus.Running)]
    public async Task RealCliSqliteTransitionFailureReturnsStorageErrorWithoutInventingTerminalResult(string stage, int rejected, RunStatus saved)
    {
        await using var test = await WindowsCrashCase.CreateAsync(checkpointStage: stage);
        await test.StartAsync();
        await test.ReadyAsync();
        if (stage == "before-terminal") { test.Signal("Release"); test.Signal("RootExit"); }
        using var checkpoint = await test.AtCheckpointAsync();
        var snapshots = await test.SnapshotsAsync();
        await test.SqlAsync($"CREATE TRIGGER reject_crash_transition BEFORE UPDATE ON Runs WHEN NEW.Status = {rejected} BEGIN SELECT RAISE(ABORT, 'Synthetic crash-test storage failure'); END;");
        await test.ContinueAsync();
        await test.Cli.WaitForExitAsync().WaitAsync(WindowsCrashCase.Limit);
        Assert.Equal(4, test.Cli.ExitCode);
        Assert.DoesNotContain("Succeeded", await test.Stdout!);
        Assert.Contains("Cannot read configured storage or save changes", await test.Stderr!);
        await Task.WhenAll(test.Children.Prepend(test.Root!).Select(p => p.WaitForExitAsync())).WaitAsync(WindowsCrashCase.Limit);
        Assert.True(File.Exists(test.FinallyFile)); // This control case returns normally, unlike TerminateProcess cases.
        Assert.Equal(saved, (await test.Store.GetRunAsync(test.Run.Id))!.Status);
        await AssertRecoveredAsync(test, snapshots);
    }

    [WindowsFact]
    public async Task RecoveryNeverSignalsRecordedPidEvenWhenItNamesAnUnrelatedLiveFixture()
    {
        await using var test = await WindowsCrashCase.CreateAsync();
        await test.StartAsync(); await test.ReadyAsync(); await test.SavedStatusAsync(RunStatus.Running);
        await test.KillCliAsync(); await test.AssertGroupExitedAsync();
        using var sentinel = Process.Start(new ProcessStartInfo(WindowsCrashCase.FixtureExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "native-hold", Path.Combine(test.Workspace.Root, "sentinel-ready") }
        })!;
        _ = sentinel.Handle;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var decoy = Run.Create(RunId.New(), test.Experiment, test.Run.ExperimentSnapshot.Task,
                test.Run.ParametersSnapshot, test.Run.SourceSnapshot, test.Run.EnvironmentSnapshot, now);
            using (var ended = test.Store.OpenRunSession())
            {
                await ended.AddRunAsync(decoy);
                decoy.Start(now, sentinel.Id, decoy.Version); // Explicit synthetic stale/reused-PID stand-in.
                await test.Store.SaveRunAsync(decoy, 0);
            }
            var recovered = await test.RecoverAsync();
            Assert.Equal(RunStatus.Interrupted, recovered.Status);
            Assert.Equal(RunStatus.Interrupted, (await test.Store.GetRunAsync(decoy.Id))!.Status);
            Assert.False(sentinel.HasExited);
            await test.RecoverAsync(expectRecovered: false);
            Assert.False(sentinel.HasExited);
        }
        finally { if (!sentinel.HasExited) { WindowsCrashCase.KillOnly(sentinel); await sentinel.WaitForExitAsync().WaitAsync(WindowsCrashCase.Limit); } }
    }

    [WindowsTheory]
    [InlineData("ordinary")]
    [InlineData("console")]
    [InlineData("nested")]
    public async Task CliCrashWorksFromOrdinaryParentConsoleAndCompatibleNestedJob(string context)
    {
        await using var test = await WindowsCrashCase.CreateAsync();
        var stem = Path.Combine(test.Workspace.Root, "parent");
        string[] arguments = ["crash-cli-parent", context == "console" ? "console" : "ordinary", stem, test.Prefix,
            test.CliDll, .. test.CliArguments];
        Process? parent = null;
        WindowsNativeProcess? outer = null;
        Task<string>? outerOutput = null;
        Task<string>? outerError = null;
        try
        {
            if (context == "nested")
            {
                outer = new WindowsNativeProcessLauncher().Launch(WindowsCrashCase.FixtureExe, arguments,
                    test.Workspace.Root, "{}", test.Workspace.Root);
                outerOutput = new StreamReader(outer.StandardOutput).ReadToEndAsync();
                outerError = new StreamReader(outer.StandardError).ReadToEndAsync();
            }
            else
            {
                var start = new ProcessStartInfo(WindowsCrashCase.FixtureExe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                parent = Process.Start(start)!;
            }
            using var deadline = new CancellationTokenSource(WindowsCrashCase.Limit);
            var pid = 0;
            while (pid == 0)
            {
                try { if (File.Exists(stem + ".pid")) int.TryParse(await File.ReadAllTextAsync(stem + ".pid", deadline.Token), out pid); }
                catch (IOException) { }
                if (pid == 0) await Task.Delay(10, deadline.Token);
            }
            test.AttachCli(pid);
            await test.ReadyAsync(); await test.SavedStatusAsync(RunStatus.Running);
            Assert.True(IsProcessInJob(test.Cli.Handle, 0, out var ambient));
            TestContext.Current.TestOutputHelper?.WriteLine($"Synthetic context={context}; Windows={Environment.OSVersion.Version}; CLI in ambient Job={ambient}.");
            if (outer is not null)
            {
                Assert.True(IsProcessInJob(test.Root!.Handle, outer.Job.DangerousGetHandle(), out var member));
                Assert.True(member);
            }
            var snapshots = await test.SnapshotsAsync();
            await test.KillCliAsync(); await test.AssertGroupExitedAsync();
            await test.EventAsync("ParentObservedExit");
            if (parent is not null) Assert.False(parent.HasExited);
            if (outer is not null) Assert.Equal(258u, WindowsNativeMethods.WaitForSingleObject(outer.Process, 0));
            Assert.DoesNotContain("Succeeded", await File.ReadAllTextAsync(stem + ".stdout"));
            await AssertRecoveredAsync(test, snapshots);
            // Parent and outer Job have remained alive throughout the kill-on-last-close proof.
            var evidence = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "windows-crash-context-" + context + "-" + test.Token + ".json"),
                JsonSerializer.Serialize(new
                {
                    SyntheticCrashTest = true,
                    Context = context,
                    CliInAmbientJob = ambient,
                    ControlledOuterJobRemainedOpen = outer is not null,
                    OS = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.OSArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    CliOnlyTerminationAndRecoveryPassed = true
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (test.Cli is not null && !test.Cli.HasExited) { WindowsCrashCase.KillOnly(test.Cli); await test.Cli.WaitForExitAsync().WaitAsync(WindowsCrashCase.Limit); }
            test.Signal("ParentRelease");
            if (parent is not null)
            {
                try { await parent.WaitForExitAsync().WaitAsync(WindowsCrashCase.Limit); }
                finally { if (!parent.HasExited) WindowsCrashCase.KillOnly(parent); parent.Dispose(); }
            }
            if (outer is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(WindowsCrashCase.Limit);
                    await outer.WaitForRootExitAsync(timeout.Token);
                    await Task.WhenAll(outerOutput!, outerError!).WaitAsync(WindowsCrashCase.Limit);
                }
                finally { outer.Dispose(); }
            }
        }
    }

    private static async Task AssertRecoveredAsync(WindowsCrashCase test, string[] snapshots)
    {
        var recovered = await test.RecoverAsync();
        Assert.Equal(RunStatus.Interrupted, recovered.Status);
        Assert.Null(recovered.ExitCode);
        Assert.Equal(test.Run.ProcessId, recovered.ProcessId);
        Assert.Equal(test.Run.StartedAtUtc, recovered.StartedAtUtc);
        Assert.Equal(snapshots, await test.SnapshotsAsync());
        var again = await test.RecoverAsync(expectRecovered: false);
        Assert.Equal((recovered.Version, recovered.FinishedAtUtc, recovered.FailureReason),
            (again.Version, again.FinishedAtUtc, again.FailureReason));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool member);
}
