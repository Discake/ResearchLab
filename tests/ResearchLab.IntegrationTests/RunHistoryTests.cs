using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class RunHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyHistoryAndUnknownIdentifiersHaveDistinctResults()
    {
        using var fixture = new TestWorkspace();
        var (experiment, _) = await SeedHistoryAsync(fixture);
        var history = new RunHistory(fixture.Store);
        Assert.Empty(await history.ListRunsAsync(experiment.Id, cancellationToken: Token));
        Assert.Equal("RunNotFound", (await Assert.ThrowsAsync<CatalogException>(() =>
            history.GetRunAsync(RunId.New(), Token))).Code);
        Assert.Equal("ExperimentNotFound", (await Assert.ThrowsAsync<CatalogException>(() =>
            history.ListRunsAsync(ExperimentId.New(), cancellationToken: Token))).Code);
    }

    [Fact]
    public async Task HistoryFiltersSortsAndPagesInSqliteAcrossReopening()
    {
        using var fixture = new TestWorkspace();
        var (experiment, template) = await SeedHistoryAsync(fixture);
        var first = MakeRun(experiment, template, 1, TestWorkspace.Epoch);
        var tieHigh = MakeRun(experiment, template, 3, TestWorkspace.Epoch.AddTicks(2));
        var tieLow = MakeRun(experiment, template, 2, TestWorkspace.Epoch.AddTicks(2).ToOffset(TimeSpan.FromHours(3)));
        var latest = MakeRun(experiment, template, 4, TestWorkspace.Epoch.AddSeconds(1));
        // Deliberately insert out of chronological and ID order. The seeded Run belongs
        // to a different Experiment and must not appear in these pages.
        foreach (var run in new[] { tieHigh, first, latest, tieLow })
            await fixture.Store.AddRunAsync(run, Token);
        var history = new RunHistory(new SqliteResearchStore(fixture.Database));
        var all = await history.ListRunsAsync(experiment.Id, new(0, 100), Token);
        Assert.Equal(new[] { latest.Id, tieLow.Id, tieHigh.Id, first.Id }, all.Select(run => run.Id));
        var pageOne = await history.ListRunsAsync(experiment.Id, new(0, 2), Token);
        var pageTwo = await history.ListRunsAsync(experiment.Id, new(2, 2), Token);
        Assert.Equal(all.Select(run => run.Id), pageOne.Concat(pageTwo).Select(run => run.Id));
        Assert.Single(await history.ListRunsAsync(experiment.Id, new(3, 2), Token));
        Assert.Empty(await history.ListRunsAsync(experiment.Id, new(4, 2), Token));
        Assert.Empty(await history.ListRunsAsync(experiment.Id, new(int.MaxValue, 100), Token));
        Assert.Equal(pageOne.Select(run => run.Id),
            (await history.ListRunsAsync(experiment.Id, new(0, 2), Token)).Select(run => run.Id));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 101)]
    [InlineData(0, int.MaxValue)]
    public void InvalidPaginationIsRejected(int offset, int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunPageRequest(offset, size));

    [Fact]
    public async Task DefaultAndMaximumPageSizesBoundMaterializedHistory()
    {
        using var fixture = new TestWorkspace();
        var (experiment, template) = await SeedHistoryAsync(fixture);
        for (var index = 1; index <= 105; index++)
            await fixture.Store.AddRunAsync(MakeRun(experiment, template, index, TestWorkspace.Epoch), Token);
        var history = new RunHistory(fixture.Store);
        Assert.Equal(50, (await history.ListRunsAsync(experiment.Id, cancellationToken: Token)).Count);
        Assert.Equal(100, (await history.ListRunsAsync(experiment.Id, new(0, 100), Token)).Count);
        Assert.Equal(5, (await history.ListRunsAsync(experiment.Id, new(100, 100), Token)).Count);
    }

    [Fact]
    public async Task SqliteLimitsRowsBeforeDeserializingSnapshots()
    {
        using var fixture = new TestWorkspace();
        var (experiment, template) = await SeedHistoryAsync(fixture);
        var older = MakeRun(experiment, template, 1, TestWorkspace.Epoch);
        var newer = MakeRun(experiment, template, 2, TestWorkspace.Epoch.AddSeconds(1));
        await fixture.Store.AddRunAsync(older, Token);
        await fixture.Store.AddRunAsync(newer, Token);
        await using var db = Open(fixture);
        // A corrupt excluded row would fail if the adapter loaded all history before paging.
        var invalidJson = "Synthetic invalid JSON";
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Runs SET ParametersSnapshot = {invalidJson} WHERE Id = {older.Id.Value}", Token);
        var page = await new RunHistory(fixture.Store).ListRunsAsync(experiment.Id, new(0, 1), Token);
        Assert.Equal(newer.Id, Assert.Single(page).Id);
    }

    [Theory]
    [InlineData(RunStatus.Created)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Canceled)]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.Interrupted)]
    public async Task DetailsAndListAreSafeSnapshotsAndNeverWrite(RunStatus status)
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        if (status != RunStatus.Created)
        {
            run.Start(TestWorkspace.Epoch, 42, run.Version);
            run.AddMetric("synthetic", 1, TestWorkspace.Epoch, run.Version);
            switch (status)
            {
                case RunStatus.Succeeded: run.Succeed(TestWorkspace.Epoch, 0, true, run.Version); break;
                case RunStatus.Failed: run.Fail(TestWorkspace.Epoch, "secret /private/path token=fixture", run.Version, 7); break;
                case RunStatus.Canceled: run.Cancel(TestWorkspace.Epoch, run.Version); break;
                case RunStatus.TimedOut: run.TimeOut(TestWorkspace.Epoch.AddSeconds(10), run.Version); break;
                case RunStatus.Interrupted: run.Interrupt(TestWorkspace.Epoch, "secret /private/path", run.Version); break;
            }
            await fixture.Store.SaveRunAsync(run, 0, Token);
        }
        await using var db = Open(fixture);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER forbid_history_update BEFORE UPDATE ON Runs
            BEGIN SELECT RAISE(ABORT, 'History must never update Runs'); END;
            """, Token);
        var history = new RunHistory(fixture.Store);
        var details = await history.GetRunAsync(run.Id, Token);
        var listed = Assert.Single(await history.ListRunsAsync(run.ExperimentId, cancellationToken: Token));
        foreach (var result in new[] { details, listed })
        {
            Assert.Equal(run.Id, result.Id);
            Assert.Equal(run.ExperimentId, result.ExperimentId);
            Assert.Equal(status, result.Status);
            Assert.Equal(run.CreatedAtUtc, result.CreatedAtUtc);
            Assert.Equal(run.StartedAtUtc, result.StartedAtUtc);
            Assert.Equal(run.FinishedAtUtc, result.FinishedAtUtc);
            Assert.Equal(run.ExitCode, result.ExitCode);
            Assert.Equal(ParameterValue.From(2), result.Parameters["n"]);
            Assert.DoesNotContain("secret", result.Diagnostic ?? "");
            Assert.DoesNotContain("/private/path", result.Diagnostic ?? "");
            var dictionary = Assert.IsAssignableFrom<IDictionary<string, ParameterValue>>(result.Parameters);
            Assert.Throws<NotSupportedException>(() => dictionary["n"] = ParameterValue.From(999));
        }
        var reloaded = (await fixture.Store.GetRunAsync(run.Id, Token))!;
        Assert.Equal(run.Version, reloaded.Version);
        Assert.Equal(run.FailureReason, reloaded.FailureReason);
        Assert.Equal(run.Metrics.Select(point => point.Id), reloaded.Metrics.Select(point => point.Id));
        Assert.Equal(run.ParametersSnapshot["n"], reloaded.ParametersSnapshot["n"]);
    }

    [Fact]
    public async Task ExistingLogDescriptorsRemainAvailableAlongsideHistory()
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        var root = Path.Combine(fixture.Root, "logs");
        var logs = new RunLogs(fixture.Store, new RunLogFiles(root));
        Assert.Null(await logs.GetPathsAsync(run.Id, Token));
        var directory = Path.Combine(root, run.Id.Value.ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), "Synthetic stdout", Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), "Synthetic stderr", Token);
        var details = await new RunHistory(fixture.Store).GetRunAsync(run.Id, Token);
        var paths = (await logs.GetPathsAsync(details.Id, Token))!;
        Assert.False(Path.IsPathRooted(paths.Stdout));
        await using var stream = await logs.OpenReadAsync(details.Id, RunLogStream.Stderr, Token);
        using var reader = new StreamReader(stream);
        Assert.Equal("Synthetic stderr", await reader.ReadToEndAsync(Token));
    }

    private static async Task<(Experiment, Run)> SeedHistoryAsync(TestWorkspace fixture)
    {
        var (project, _, template) = await fixture.SeedRunAsync();
        var experiment = new Experiment(ExperimentId.New(), project.Id, "Synthetic history", "", "benchmark", [], TestWorkspace.Epoch);
        await fixture.Store.AddExperimentAsync(experiment, Token);
        return (experiment, template);
    }

    private static Run MakeRun(Experiment experiment, Run template, int id, DateTimeOffset created) =>
        Run.Create(new RunId(Guid.Parse($"00000000-0000-0000-0000-{id:D12}")), experiment,
            template.ExperimentSnapshot.Task, new Dictionary<string, ParameterValue>(),
            template.SourceSnapshot, template.EnvironmentSnapshot, created);

    private static ResearchDbContext Open(TestWorkspace fixture) => new(new DbContextOptionsBuilder<ResearchDbContext>()
        .UseSqlite($"Data Source={fixture.Database};Pooling=False").Options);
}
