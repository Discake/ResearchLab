using Xunit;

namespace ResearchLab.Domain.Tests;

public sealed class MetricTests
{
    [Fact]
    public void ArrivalSequencePreservesRepeatedStepsAndTimestampsAndSurvivesFailure()
    {
        var run = DomainFixtures.Running();
        var at = DomainFixtures.Epoch.AddSeconds(2);
        var first = run.AddMetric("residual", 0.5, at, run.Version, step: 1, unit: "1");
        var second = run.AddMetric("residual", 0.4, at, run.Version, step: 1, unit: "1");
        var third = run.AddMetric("duration", 5, at, run.Version, unit: "ms");
        run.Fail(at, "Fixture failure", run.Version, 2);

        Assert.Equal(new long[] { 1, 2, 3 }, run.Metrics.Select(metric => metric.Sequence));
        Assert.Equal(new[] { first, second, third }, run.Metrics);
        Assert.Equal(first.Step, second.Step);
        Assert.Equal(first.RecordedAtUtc, second.RecordedAtUtc);
        Assert.All(run.Metrics, metric => Assert.Equal(run.Id, metric.RunId));
        Assert.Equal(3, run.Metrics.Select(metric => metric.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteMetricWithoutConsumingSequenceOrVersion(double value)
    {
        var run = DomainFixtures.Running();
        Assert.Equal("NonFiniteMetric", Assert.Throws<DomainException>(() =>
            run.AddMetric("error", value, DomainFixtures.Epoch, run.Version)).Code);
        Assert.Empty(run.Metrics);
        Assert.Equal(1, run.Version);
        Assert.Equal(1, run.AddMetric("error", 0, DomainFixtures.Epoch, run.Version).Sequence);
    }

    [Theory]
    [InlineData("ms", "s")]
    [InlineData("ms", null)]
    [InlineData(null, "ms")]
    [InlineData("ms", "MS")]
    public void RejectsUnitChangesWithinSeries(string? original, string? changed)
    {
        var run = DomainFixtures.Running();
        run.AddMetric("duration", 1, DomainFixtures.Epoch, run.Version, unit: original);
        Assert.Equal("IncompatibleMetricUnit", Assert.Throws<DomainException>(() =>
            run.AddMetric("duration", 2, DomainFixtures.Epoch, run.Version, unit: changed)).Code);
        Assert.Single(run.Metrics);
        Assert.Equal(2, run.Version);
    }

    [Fact]
    public void RejectsBlankNamesUnitsAndStaleWrites()
    {
        var run = DomainFixtures.Running();
        Assert.Throws<DomainException>(() => run.AddMetric(" ", 1, DomainFixtures.Epoch, run.Version));
        Assert.Throws<DomainException>(() => run.AddMetric("a", 1, DomainFixtures.Epoch, run.Version, unit: " "));
        Assert.Equal("VersionConflict", Assert.Throws<DomainException>(() =>
            run.AddMetric("a", 1, DomainFixtures.Epoch, 0)).Code);
        Assert.Empty(run.Metrics);
        Assert.Equal(1, run.Version);
    }

    [Fact]
    public void DoesNotAcceptMetricsBeforeStartOrAfterCompletion()
    {
        var run = DomainFixtures.Run();
        Assert.Throws<DomainException>(() => run.AddMetric("a", 1, DomainFixtures.Epoch, run.Version));
        run.Start(DomainFixtures.Epoch, 1, run.Version);
        run.AddMetric("a", 1, DomainFixtures.Epoch, run.Version);
        run.Succeed(DomainFixtures.Epoch, 0, true, run.Version);
        Assert.Throws<DomainException>(() => run.AddMetric("a", 2, DomainFixtures.Epoch, run.Version));
        Assert.Single(run.Metrics);
    }
}
