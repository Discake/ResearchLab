using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

// Synthetic observations only; every test uses a real temporary SQLite database.
public sealed class RunMetricIngestionTests
{
    private static MetricEvent Metric(string name = "residual", double value = 1, string? unit = "1") =>
        new(name, value, 7, unit);

    private static async Task<Run> RunningAsync(TestWorkspace fixture)
    {
        var (_, _, run) = await fixture.SeedRunAsync();
        run.Start(TestWorkspace.Epoch, 123, run.Version);
        await fixture.Store.SaveRunAsync(run, 0, cancellationToken: TestContext.Current.CancellationToken);
        return run;
    }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; } = TimeSpan.FromSeconds(20);
        public override DateTimeOffset GetUtcNow() => TestWorkspace.Epoch + Elapsed;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public async Task OneParsedEventIsBufferedThenDurableAfterReopen()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, timeProvider: new Clock(), cancellationToken: TestContext.Current.CancellationToken);
        var metric = Assert.IsType<MetricParseResult.Metric>(MetricProtocol.Parse(
            "@researchlab/v1 {\"type\":\"metric\",\"name\":\"residual\",\"value\":0.25}"u8)).Event;
        Assert.Null(await ingestion.AcceptAsync(metric, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty((await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!.Metrics);
        var result = await ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SavedCount);
        Assert.Empty(result.Rejections);
        Assert.Equal(2, result.Version);
        var reopened = await new SqliteResearchStore(fixture.Database, readOnly: true).GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken);
        var point = Assert.Single(reopened!.Metrics);
        Assert.Equal(0.25, point.Value);
        Assert.Equal(1, point.Sequence);
        Assert.Null(point.Step);
        Assert.Null(point.Unit);
        Assert.Equal(TestWorkspace.Epoch.AddSeconds(20), point.RecordedAtUtc);
    }

    [Fact]
    public async Task SeriesAndRepeatedStepsAndTimestampsKeepGlobalArrivalOrderAcrossBatches()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var clock = new Clock();
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, batchSize: 2, timeProvider: clock, cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(value: 3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, (await ingestion.AcceptAsync(Metric("duration", 8, "ms"), cancellationToken: TestContext.Current.CancellationToken))!.SavedCount);
        await ingestion.AcceptAsync(Metric(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(value: 1), cancellationToken: TestContext.Current.CancellationToken);
        var points = (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!.Metrics;
        Assert.Equal(new long[] { 1, 2, 3, 4 }, points.Select(point => point.Sequence));
        Assert.Equal(new[] { "residual", "duration", "residual", "residual" }, points.Select(point => point.Name));
        Assert.All(points, point => Assert.Equal(7, point.Step));
        Assert.Single(points.Select(point => point.RecordedAtUtc).Distinct());
        Assert.Equal(5, ingestion.Version);
    }

    [Theory]
    [InlineData("ms", "s")]
    [InlineData("ms", null)]
    [InlineData(null, "ms")]
    public async Task RejectsChangedUnitsAcrossWindowsAndPreservesValidPoints(string? unit, string? changed)
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, timeProvider: new Clock(), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(unit: unit), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(unit: changed), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(value: 2, unit: unit), cancellationToken: TestContext.Current.CancellationToken);
        var result = await ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SavedCount);
        Assert.Equal(new MetricRejection(0, "IncompatibleMetricUnit"), Assert.Single(result.Rejections));
        var saved = (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(new double[] { 1, 2 }, saved.Metrics.Select(point => point.Value));
        Assert.Equal(new long[] { 1, 2 }, saved.Metrics.Select(point => point.Sequence));
        Assert.Equal(3, saved.Version);
    }

    [Fact]
    public async Task InvalidEventBetweenValidEventsInSameBatchDoesNotConsumeSequenceOrVersion()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(unit: "s"), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        var result = await ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.SavedCount);
        Assert.Equal(new MetricRejection(1, "IncompatibleMetricUnit"), Assert.Single(result.Rejections));
        Assert.Equal(3, result.Version);
        Assert.Equal(new long[] { 1, 2 }, (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!.Metrics.Select(point => point.Sequence));
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Canceled)]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.Succeeded)]
    public async Task CompletionFlushesEvidenceAndNeverRewritesIt(RunStatus status)
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, timeProvider: new Clock(), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        var original = Assert.Single((await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!.Metrics);
        await ingestion.AcceptAsync(Metric(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        var process = status switch
        {
            RunStatus.Failed => new ProcessResult(2, false, "Synthetic failure"),
            RunStatus.Canceled => new ProcessResult(null, false, null, Canceled: true),
            RunStatus.TimedOut => new ProcessResult(null, true, null),
            _ => new ProcessResult(0, false, null)
        };
        var result = await ingestion.CompleteAsync(process, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SavedCount);
        Assert.Equal(4, result.Version);
        var saved = (await new SqliteResearchStore(fixture.Database, readOnly: true).GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(status, saved.Status);
        Assert.Equal(2, saved.Metrics.Count);
        Assert.Equal(original.Id, saved.Metrics[0].Id);
        Assert.Equal(original.Value, saved.Metrics[0].Value);
        Assert.Equal(original.RecordedAtUtc, saved.Metrics[0].RecordedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("InvalidMetricState", (await Assert.ThrowsAsync<CatalogException>(() =>
            RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task CreatedRunRejectsOpeningIngestion()
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        Assert.Equal("InvalidMetricState", (await Assert.ThrowsAsync<CatalogException>(() =>
            RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task ReadOnlySqliteFailureIsNotReportedAsSuccessAndStopsOwner()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var ingestion = await RunMetricIngestion.OpenAsync(new SqliteResearchStore(fixture.Database, readOnly: true), run.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, ingestion.Version);
        Assert.Equal(1, ingestion.PendingCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken));
        var saved = (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Empty(saved.Metrics);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task InsertFailureRollsBackAllPointsAndVersion()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await using var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=False");
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_metric BEFORE INSERT ON MetricPoints WHEN NEW.Sequence = 2 BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END";
        await command.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken));
        var saved = (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(1, saved.Version);
        Assert.Empty(saved.Metrics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetricAndTerminalWindowsShareAtomicVersionFence(bool metricWins)
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await using var metrics = await fixture.Store.OpenRunMetricWriteAsync(run.Id, ["residual"], 1, cancellationToken: TestContext.Current.CancellationToken);
        await using var terminal = await fixture.Store.OpenRunMetricWriteAsync(run.Id, [], 1, cancellationToken: TestContext.Current.CancellationToken);
        metrics.Run.AddMetric("residual", 1, TestWorkspace.Epoch, metrics.Run.Version, unit: "1");
        terminal.Run.Fail(TestWorkspace.Epoch.AddSeconds(1), "Synthetic failure", terminal.Run.Version);
        await (metricWins ? metrics : terminal).SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        var loser = metricWins ? terminal : metrics;
        Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogException>(() => loser.SaveAsync(cancellationToken: TestContext.Current.CancellationToken))).Code);
        var saved = (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(2, saved.Version);
        Assert.Equal(metricWins ? RunStatus.Running : RunStatus.Failed, saved.Status);
        Assert.Equal(metricWins ? 1 : 0, saved.Metrics.Count);
    }

    [Fact]
    public async Task CompetingMetricOwnersCannotDuplicateSequenceOrLosePoints()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var first = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: TestContext.Current.CancellationToken);
        var second = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: TestContext.Current.CancellationToken);
        await first.AcceptAsync(Metric(value: 1), cancellationToken: TestContext.Current.CancellationToken);
        await second.AcceptAsync(Metric(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        await first.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogException>(() => second.FlushAsync(cancellationToken: TestContext.Current.CancellationToken))).Code);
        Assert.Equal(1, Assert.Single((await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!.Metrics).Value);
    }

    [Fact]
    public async Task TerminalSaveFailureLeavesPreviouslyFlushedEvidenceDurable()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await using var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=False");
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_terminal BEFORE UPDATE ON Runs WHEN NEW.Status <> 1 BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END";
        await command.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, timeProvider: new Clock(), cancellationToken: TestContext.Current.CancellationToken);
        await ingestion.AcceptAsync(Metric(), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.CompleteAsync(new ProcessResult(2, false, "Fixture"), cancellationToken: TestContext.Current.CancellationToken));
        var saved = (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Running, saved.Status);
        Assert.Single(saved.Metrics);
        Assert.Equal(2, saved.Version);
    }

    [Fact]
    public async Task BatchesStayBoundedAndIntervalAndExplicitFlushPersistWithoutFullHistory()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var clock = new Clock();
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, batchSize: 3, timeProvider: clock, cancellationToken: TestContext.Current.CancellationToken);
        for (var index = 0; index < 600; index++)
        {
            var result = await ingestion.AcceptAsync(Metric(value: index), cancellationToken: TestContext.Current.CancellationToken);
            Assert.InRange(ingestion.PendingCount, 0, 2);
            if (index % 3 == 2)
                Assert.Equal(3, result!.SavedCount);
            else
                Assert.Null(result);
        }
        await using (var window = await fixture.Store.OpenRunMetricWriteAsync(run.Id, ["residual"], 601, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Empty(window.Run.Metrics);
            Assert.Equal(601, window.Run.AddMetric("residual", 1, clock.GetUtcNow(), window.Run.Version, unit: "1").Sequence);
        }
        await ingestion.AcceptAsync(Metric(value: 600), cancellationToken: TestContext.Current.CancellationToken);
        clock.Elapsed += TimeSpan.FromSeconds(2);
        Assert.Equal(2, (await ingestion.AcceptAsync(Metric(value: 601), cancellationToken: TestContext.Current.CancellationToken))!.SavedCount);
        Assert.Equal(0, (await ingestion.FlushAsync(cancellationToken: TestContext.Current.CancellationToken)).SavedCount);
        Assert.Equal(602, (await fixture.Store.GetRunAsync(run.Id, cancellationToken: TestContext.Current.CancellationToken))!.Metrics.Count);
        Assert.Equal(603, ingestion.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public async Task RejectsUnboundedBatchConfiguration(int size)
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunMetricIngestion.OpenAsync(fixture.Store, run.Id, size, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AlreadyOpenMetricWindowsCannotCommitTheSameSequence()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await using var first = await fixture.Store.OpenRunMetricWriteAsync(run.Id, ["residual"], 1,
            TestContext.Current.CancellationToken);
        await using var second = await fixture.Store.OpenRunMetricWriteAsync(run.Id, ["residual"], 1,
            TestContext.Current.CancellationToken);
        first.Run.AddMetric("residual", 1, TestWorkspace.Epoch, 1);
        second.Run.AddMetric("residual", 2, TestWorkspace.Epoch, 1);
        await first.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogException>(() =>
            second.SaveAsync(TestContext.Current.CancellationToken))).Code);
        Assert.Equal(1, Assert.Single((await fixture.Store.GetRunAsync(run.Id,
            TestContext.Current.CancellationToken))!.Metrics).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyTerminalSaveAndMetricWindowUseTheSameFence(bool metricWins)
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await using var window = await fixture.Store.OpenRunMetricWriteAsync(run.Id, ["residual"], 1,
            TestContext.Current.CancellationToken);
        window.Run.AddMetric("residual", 1, TestWorkspace.Epoch, 1);
        run.Fail(TestWorkspace.Epoch, "Synthetic failure", 1);
        if (metricWins)
        {
            await window.SaveAsync(TestContext.Current.CancellationToken);
            Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogException>(() =>
                fixture.Store.SaveRunAsync(run, 1, TestContext.Current.CancellationToken))).Code);
        }
        else
        {
            await fixture.Store.SaveRunAsync(run, 1, TestContext.Current.CancellationToken);
            Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogException>(() =>
                window.SaveAsync(TestContext.Current.CancellationToken))).Code);
        }
    }

    [Fact]
    public async Task NewOwnerContinuesSequenceAndRejectsPersistedUnitWithoutMaterializingOtherSeries()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        var first = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        for (var index = 0; index < 256; index++)
            await first.AcceptAsync(Metric($"series-{index}"), TestContext.Current.CancellationToken);
        var second = await RunMetricIngestion.OpenAsync(new SqliteResearchStore(fixture.Database), run.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        await second.AcceptAsync(Metric("series-0", unit: "s"), TestContext.Current.CancellationToken);
        await second.AcceptAsync(Metric("series-255", value: 2), TestContext.Current.CancellationToken);
        var result = await second.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SavedCount);
        Assert.Equal("IncompatibleMetricUnit", Assert.Single(result.Rejections).Code);
        var saved = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(257, saved.Metrics.Count);
        Assert.Equal(257, saved.Metrics[^1].Sequence);
        Assert.Equal(258, saved.Version);
    }

    [Fact]
    public async Task WriteWindowCannotOverflowItsBoundOrUseLegacySave()
    {
        using var fixture = new TestWorkspace();
        var run = await RunningAsync(fixture);
        await using var window = await fixture.Store.OpenRunMetricWriteAsync(run.Id, ["residual"], 1,
            TestContext.Current.CancellationToken);
        for (var index = 0; index < RunMetricIngestion.MaxBatchSize; index++)
            window.Run.AddMetric("residual", index, TestWorkspace.Epoch, window.Run.Version);
        Assert.Equal("MetricWindowExceeded", Assert.Throws<DomainException>(() =>
            window.Run.AddMetric("residual", 0, TestWorkspace.Epoch, window.Run.Version)).Code);
        Assert.Equal(RunMetricIngestion.MaxBatchSize, window.Run.Metrics.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.SaveRunAsync(window.Run, 1, TestContext.Current.CancellationToken));
        await window.SaveAsync(TestContext.Current.CancellationToken);
    }
}
