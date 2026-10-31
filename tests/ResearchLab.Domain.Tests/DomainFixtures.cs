namespace ResearchLab.Domain.Tests;

internal static class DomainFixtures
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static Experiment Experiment(params ParameterDefinition[] definitions) => new(
        ExperimentId.New(), ProjectId.New(), "Test experiment", "Test fixture, not research evidence",
        "benchmark", definitions, Epoch);

    public static TaskSnapshot Task() => new("benchmark", "fixture-not-executed", [], TimeSpan.FromSeconds(10));

    public static Run Run() => Domain.Run.Create(RunId.New(), Experiment(), Task(),
        new Dictionary<string, ParameterValue>(), new SourceSnapshot(null, null, null),
        new EnvironmentSnapshot(), Epoch);

    public static Run Running()
    {
        var run = Run();
        run.Start(Epoch.AddSeconds(1), 123, run.Version);
        return run;
    }
}
