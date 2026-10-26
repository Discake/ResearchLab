using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed partial class ForegroundCliTests
{
    [Fact]
    public async Task RealMetricExecutionCanBeQueriedByNewCliProcessesWithoutWrites()
    {
        using var fixture = new TestWorkspace();
        var experiment = await SetupAsync(fixture, native: true);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ProcessFixture")))
            File.Copy(file, Path.Combine(fixture.Workspace, Path.GetFileName(file)), overwrite: true);
        fixture.WriteConfiguration(OperatingSystem.IsWindows() ? "ResearchLab.ProcessFixture.exe" : "ResearchLab.ProcessFixture", ["metric-early"]);
        var start = await NativeAsync(fixture, "yes\n", "run", "start", experiment.ToString());
        Assert.True(start.Code == 0, start.Error);
        using var started = JsonDocument.Parse(start.Output);
        var id = started.RootElement.GetProperty("id").GetGuid();
        var before = await File.ReadAllBytesAsync(fixture.Database, Token);
        var result = await NativeAsync(fixture, "", "run", "metrics", id.ToString(), "--page-size", "1");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(id, json.RootElement.GetProperty("runId").GetGuid());
        Assert.True(json.RootElement.GetProperty("hasMore").GetBoolean());
        var first = Assert.Single(json.RootElement.GetProperty("points").EnumerateArray());
        Assert.Equal(0.5, first.GetProperty("value").GetDouble());
        Assert.Equal(1, first.GetProperty("sequence").GetInt64());
        Assert.Equal("1", first.GetProperty("unit").GetString());
        Assert.True(first.GetProperty("recordedAtUtc").GetDateTimeOffset() > TestWorkspace.Epoch);
        var second = await NativeAsync(fixture, "", "run", "metrics", id.ToString(), "--name", "residual", "--offset", "1");
        using var next = JsonDocument.Parse(second.Output);
        Assert.Equal(0.2, Assert.Single(next.RootElement.GetProperty("points").EnumerateArray()).GetProperty("value").GetDouble());
        Assert.False(next.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Database, Token));
        Assert.Equal(3, (await NativeAsync(fixture, "", "run", "metrics", Guid.NewGuid().ToString())).Code);
        Assert.Equal(2, (await NativeAsync(fixture, "", "run", "metrics", id.ToString(), "--page-size", "101")).Code);
    }
}

public sealed class MetricQueryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyMissingFilteredRepeatedStepsAndRunIsolationAreExplicit()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, run) = await fixture.SeedRunAsync();
        IRunMetricReader reader = new SqliteResearchStore(fixture.Database, readOnly: true);
        Assert.Empty((await reader.GetMetricsAsync(run.Id, cancellationToken: Token)).Points);
        run.Start(TestWorkspace.Epoch, 123, run.Version);
        await fixture.Store.SaveRunAsync(run, 0, Token);
        var ingestion = await RunMetricIngestion.OpenAsync(fixture.Store, run.Id, cancellationToken: Token);
        await ingestion.AcceptAsync(new MetricEvent("a", 4, 1, "1"), Token);
        await ingestion.AcceptAsync(new MetricEvent("b", 2, null, null), Token);
        await ingestion.AcceptAsync(new MetricEvent("a", 16, 1, "1"), Token);
        await ingestion.CompleteAsync(new ProcessResult(0, false, null), Token);
        var other = Run.Create(RunId.New(), experiment, run.ExperimentSnapshot.Task,
            new Dictionary<string, ParameterValue>(), run.SourceSnapshot, run.EnvironmentSnapshot, TestWorkspace.Epoch);
        await fixture.Store.AddRunAsync(other, Token);
        Assert.Empty((await reader.GetMetricsAsync(other.Id, cancellationToken: Token)).Points);
        var all = await reader.GetMetricsAsync(run.Id, cancellationToken: Token);
        Assert.Equal(new long[] { 1, 2, 3 }, all.Points.Select(p => p.Sequence));
        Assert.Equal(new[] { "a", "b", "a" }, all.Points.Select(p => p.Name));
        Assert.Null(all.Points[1].Unit);
        Assert.Null(all.Points[1].Step);
        var first = await reader.GetMetricsAsync(run.Id, "a", new RunPageRequest(0, 1), Token);
        Assert.True(first.HasMore);
        Assert.Equal(4, Assert.Single(first.Points).Value);
        var last = await reader.GetMetricsAsync(run.Id, "a", new RunPageRequest(1, 1), Token);
        Assert.False(last.HasMore);
        Assert.Equal(3, Assert.Single(last.Points).Sequence);
        Assert.Equal(1, last.Points[0].Step);
        Assert.Empty((await reader.GetMetricsAsync(run.Id, "unknown", cancellationToken: Token)).Points);
        Assert.Empty((await reader.GetMetricsAsync(run.Id, page: new RunPageRequest(int.MaxValue), cancellationToken: Token)).Points);
        Assert.Equal("RunNotFound", (await Assert.ThrowsAsync<CatalogException>(() =>
            reader.GetMetricsAsync(RunId.New(), cancellationToken: Token))).Code);
    }
}
