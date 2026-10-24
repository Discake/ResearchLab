using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class RunRecoveryTests
{
    [Fact]
    public async Task CliRecoveryReportsSkippedOwnersAndRecoversEndedRunOnlyOnce()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var run = NewRun(experiment);
        using var active = fixture.Store.OpenRunSession();
        var activeRun = NewRun(experiment);
        await active.AddRunAsync(activeRun, TestContext.Current.CancellationToken);
        using (var ended = fixture.Store.OpenRunSession())
            await ended.AddRunAsync(run, TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await ResearchLab.Cli.ConsoleHost.RunAsync(
                ["--database", fixture.Database, "--data-dir", Path.Combine(fixture.Root, "data"), "run", "recover"],
                output, error, TestContext.Current.CancellationToken);
            Assert.Equal(0, code);
            using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
            var recovered = json.RootElement.GetProperty("recovered");
            Assert.Equal(attempt == 0 ? 1 : 0, recovered.GetArrayLength());
            if (attempt == 0) Assert.Equal(run.Id.Value, recovered[0].GetGuid());
            Assert.Equal(active.Id, json.RootElement.GetProperty("unverifiedSessions")[0].GetGuid());
            Assert.Equal(0, json.RootElement.GetProperty("conflicts").GetArrayLength());
            Assert.Contains("does not prove child process termination", error.ToString());
        }
        Assert.Equal(1, (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.Version);
        Assert.Equal(RunStatus.Created, (await fixture.Store.GetRunAsync(activeRun.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task OwnershipMigrationPreservesPreexistingUnownedRunAndDoesNotGuessItsSession()
    {
        using var fixture = new TestWorkspace();
        var (_, _, legacy) = await fixture.SeedRunAsync();
        var options = new DbContextOptionsBuilder<ResearchDbContext>()
            .UseSqlite($"Data Source={fixture.Database};Pooling=False").Options;
        // Only this disposable fixture DB: reproduce the pre-M3.3 schema with existing data.
        await using (var db = new ResearchDbContext(options))
            await db.GetService<IMigrator>().MigrateAsync("20261024005420_InitialResearchMetadata", TestContext.Current.CancellationToken);
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        var saved = (await fixture.Store.GetRunAsync(legacy.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal((legacy.Status, legacy.Version, legacy.CreatedAtUtc), (saved.Status, saved.Version, saved.CreatedAtUtc));
        Assert.Empty(await fixture.Store.ListIncompleteSessionsAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken)).Recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndedSessionRecoversOncePreservingSnapshotsProcessAndMetrics(bool started)
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, legacy) = await fixture.SeedRunAsync();
        var run = NewRun(experiment);
        Guid sessionId;
        using (var session = fixture.Store.OpenRunSession())
        {
            sessionId = session.Id;
            await session.AddRunAsync(run, TestContext.Current.CancellationToken);
            if (started)
            {
                run.Start(TestWorkspace.Epoch.AddSeconds(1), 123, run.Version);
                run.AddMetric("synthetic", 2.5, TestWorkspace.Epoch.AddSeconds(2), run.Version, unit: "s");
                await fixture.Store.SaveRunAsync(run, 0, TestContext.Current.CancellationToken);
            }
            var active = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
            Assert.Empty(active.Recovered);
            Assert.Contains(sessionId, active.UnverifiedSessions);
        }
        var first = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(run.Id, Assert.Single(first.Recovered));
        var saved = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Interrupted, saved.Status);
        Assert.Equal(run.Version + 1, saved.Version);
        Assert.Contains(sessionId.ToString("N"), saved.FailureReason!);
        Assert.Contains("termination is unknown", saved.FailureReason!);
        Assert.Equal(run.ProcessId, saved.ProcessId);
        Assert.Equal(run.StartedAtUtc, saved.StartedAtUtc);
        Assert.Null(saved.ExitCode);
        Assert.Equal(run.SourceSnapshot, saved.SourceSnapshot);
        Assert.Equal(run.ParametersSnapshot, saved.ParametersSnapshot);
        Assert.Equal((run.ExperimentSnapshot.Task.Key, run.ExperimentSnapshot.Task.Executable, run.ExperimentSnapshot.Task.Timeout),
            (saved.ExperimentSnapshot.Task.Key, saved.ExperimentSnapshot.Task.Executable, saved.ExperimentSnapshot.Task.Timeout));
        Assert.Equal(run.ExperimentSnapshot.Task.Arguments, saved.ExperimentSnapshot.Task.Arguments);
        Assert.Equal(run.Metrics.Select(point => point.Id), saved.Metrics.Select(point => point.Id));
        var again = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Empty(again.Recovered);
        var unchanged = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(saved.Version, unchanged.Version);
        Assert.Equal(saved.FinishedAtUtc, unchanged.FinishedAtUtc);
        Assert.Equal(saved.FailureReason, unchanged.FailureReason);
        Assert.Equal(RunStatus.Created, (await fixture.Store.GetRunAsync(legacy.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Canceled)]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.Interrupted)]
    public async Task TerminalRunsAreNeverChangedEvenWhenTheirSessionHasEnded(RunStatus status)
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var run = NewRun(experiment);
        using (var session = fixture.Store.OpenRunSession())
        {
            await session.AddRunAsync(run, TestContext.Current.CancellationToken);
            run.Start(TestWorkspace.Epoch, 123, run.Version);
            var at = TestWorkspace.Epoch.AddSeconds(30);
            switch (status)
            {
                case RunStatus.Succeeded: run.Succeed(at, 0, true, run.Version); break;
                case RunStatus.Failed: run.Fail(at, "Synthetic failure", run.Version, 7); break;
                case RunStatus.Canceled: run.Cancel(at, run.Version); break;
                case RunStatus.TimedOut: run.TimeOut(at, run.Version); break;
                case RunStatus.Interrupted: run.Interrupt(at, "Synthetic interruption", run.Version); break;
            }
            await fixture.Store.SaveRunAsync(run, 0, TestContext.Current.CancellationToken);
        }
        for (var index = 0; index < 2; index++)
        {
            Assert.Empty((await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken)).Recovered);
            var saved = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
            Assert.Equal((run.Status, run.Version, run.FinishedAtUtc, run.ExitCode, run.FailureReason),
                (saved.Status, saved.Version, saved.FinishedAtUtc, saved.ExitCode, saved.FailureReason));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentUpdateWinsWithoutStaleRecoveryOverwrite(bool terminalWinner)
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var run = NewRun(experiment);
        using (var session = fixture.Store.OpenRunSession())
        {
            await session.AddRunAsync(run, TestContext.Current.CancellationToken);
            run.Start(TestWorkspace.Epoch, 123, run.Version);
            await fixture.Store.SaveRunAsync(run, 0, TestContext.Current.CancellationToken);
        }
        var clock = new RecoveryClock(() =>
        {
            // A separate writer saves after recovery read V but before its version-checked save.
            var winner = fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken).GetAwaiter().GetResult()!;
            winner.AddMetric("concurrent", 7, TestWorkspace.Epoch, winner.Version);
            if (terminalWinner) winner.Succeed(TestWorkspace.Epoch, 0, true, winner.Version);
            fixture.Store.SaveRunAsync(winner, 1, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        });
        var result = await new RunRecovery(fixture.Store, clock).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Empty(result.Recovered);
        Assert.Equal(!terminalWinner, result.Conflicts.Contains(run.Id));
        var saved = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(7, Assert.Single(saved.Metrics).Value);
        Assert.Equal(terminalWinner ? RunStatus.Succeeded : RunStatus.Running, saved.Status);
        var retry = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(!terminalWinner, retry.Recovered.Contains(run.Id));
    }

    [Fact]
    public async Task ParallelRecoveryCannotClaimSameSessionOrWriteTwoTerminalVersions()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var run = NewRun(experiment);
        Guid id;
        using (var session = fixture.Store.OpenRunSession())
        {
            id = session.Id;
            await session.AddRunAsync(run, TestContext.Current.CancellationToken);
        }
        using (var firstClaim = fixture.Store.TryClaimEndedSession(id))
        {
            Assert.NotNull(firstClaim);
            var blocked = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
            Assert.Empty(blocked.Recovered);
            Assert.Contains(id, blocked.UnverifiedSessions);
        }
        var results = await Task.WhenAll(Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken),
            Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal(run.Id, Assert.Single(results.SelectMany(value => value.Recovered)));
        Assert.Equal(1, (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptSessionEvidenceIsNotAssumedToBeDead(bool corrupt)
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var run = NewRun(experiment);
        Guid id;
        using (var session = fixture.Store.OpenRunSession())
        {
            id = session.Id;
            await session.AddRunAsync(run, TestContext.Current.CancellationToken);
        }
        if (corrupt)
            await File.WriteAllTextAsync(Path.Combine(fixture.Database + ".sessions", id.ToString("N") + ".session"),
                "Synthetic corruption", TestContext.Current.CancellationToken);
        else
        {
            // Simulate unavailable storage without deleting ownership evidence.
            Directory.Move(fixture.Database + ".sessions", Path.Combine(fixture.Root, "unavailable-sessions"));
        }
        var result = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Empty(result.Recovered);
        Assert.Contains(id, result.UnverifiedSessions);
        Assert.Equal(RunStatus.Created, (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Running")]
    public async Task OtherProcessIsUntouchedWhileAliveAndRecoverableAfterActualCrash(string status)
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var executable = OperatingSystem.IsWindows() ? "ResearchLab.ProcessFixture.exe" : "ResearchLab.ProcessFixture";
        using var owner = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "owner-session", fixture.Database, experiment.Id.Value.ToString(), status }
        })!;
        var errors = owner.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var line = await owner.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(Guid.TryParse(line, out var guid), owner.HasExited ? await errors : "Synthetic owner did not acknowledge its Run.");
            var id = new RunId(guid);
            var active = await Recovery(fixture).RecoverAsync(TestContext.Current.CancellationToken);
            Assert.Empty(active.Recovered);
            Assert.Single(active.UnverifiedSessions);
            Assert.False(owner.HasExited);
            Assert.Equal(Enum.Parse<RunStatus>(status), (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status);
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(id, Assert.Single((await new RunRecovery(fixture.Store).RecoverAsync(TestContext.Current.CancellationToken)).Recovered));
            var saved = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
            Assert.Equal(RunStatus.Interrupted, saved.Status);
            Assert.Equal(status == "Running" ? owner.Id : (int?)null, saved.ProcessId);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await errors;
        }
    }

    private static Run NewRun(Experiment experiment) => Run.Create(RunId.New(), experiment,
        new TaskSnapshot("benchmark", "synthetic", [], TimeSpan.FromSeconds(10)), new Dictionary<string, ParameterValue>(),
        new SourceSnapshot("synthetic-sha", true, null),
        new EnvironmentSnapshot("synthetic", "synthetic", new Dictionary<string, string>()), TestWorkspace.Epoch);

    private static RunRecovery Recovery(TestWorkspace fixture) => new(fixture.Store, new RecoveryClock());

    private sealed class RecoveryClock(Action? onRead = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            onRead?.Invoke();
            return TestWorkspace.Epoch.AddMinutes(1);
        }
    }
}
