using Xunit;

namespace ResearchLab.Domain.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void RunRetainsValuesWhenCallerCollectionsAndExperimentChange()
    {
        var allowed = new[] { ParameterValue.From(1), ParameterValue.From(2) };
        var definitions = new[] { new ParameterDefinition("n", ParameterType.Int, allowedValues: allowed) };
        var experiment = DomainFixtures.Experiment(definitions);
        var arguments = new[] { "--original" };
        var task = new TaskSnapshot("benchmark", "never-executed", arguments, TimeSpan.FromSeconds(10));
        var input = new Dictionary<string, ParameterValue> { ["n"] = ParameterValue.From(1) };
        var toolchains = new Dictionary<string, string> { ["compiler"] = "fixture-v1" };
        var source = new SourceSnapshot("fixture-sha", true, "fixture-url");
        var run = Run.Create(RunId.New(), experiment, task, input, source,
            new EnvironmentSnapshot("fixture-os", "fixture-arch", toolchains), DomainFixtures.Epoch);

        allowed[0] = ParameterValue.From(99);
        definitions[0] = new ParameterDefinition("other", ParameterType.Bool);
        arguments[0] = "--changed";
        input["n"] = ParameterValue.From(2);
        toolchains["compiler"] = "fixture-v2";
        source = source with { GitHeadSha = "changed", IsWorkingTreeDirty = false };
        experiment.Revise(new("Changed", "", "another-task", []), DomainFixtures.Epoch.AddSeconds(1));
        run.Start(DomainFixtures.Epoch.AddSeconds(2), 123, run.Version);

        Assert.Equal(ParameterValue.From(1), run.ParametersSnapshot["n"]);
        Assert.Equal("benchmark", run.ExperimentSnapshot.Definition.TaskKey);
        Assert.Equal("Test experiment", run.ExperimentSnapshot.Definition.Name);
        Assert.Equal(ParameterValue.From(1), run.ExperimentSnapshot.Definition.ParameterDefinitions[0].AllowedValues![0]);
        Assert.Equal("--original", run.ExperimentSnapshot.Task.Arguments[0]);
        Assert.Equal("fixture-v1", run.EnvironmentSnapshot.Toolchains["compiler"]);
        Assert.Equal("fixture-sha", run.SourceSnapshot.GitHeadSha);
        Assert.True(run.SourceSnapshot.IsWorkingTreeDirty);
        Assert.NotEqual(source, run.SourceSnapshot);
        Assert.Equal(experiment.Id, run.ExperimentId);
        Assert.Equal(experiment.ProjectId, run.ExperimentSnapshot.ProjectId);
    }

    [Fact]
    public void ExposedCollectionsCannotMutateSnapshots()
    {
        var definition = new ParameterDefinition("n", ParameterType.Int, allowedValues: [ParameterValue.From(1)]);
        var experiment = DomainFixtures.Experiment(definition);
        var run = Run.Create(RunId.New(), experiment, DomainFixtures.Task(),
            new Dictionary<string, ParameterValue> { ["n"] = ParameterValue.From(1) },
            new SourceSnapshot(null, null, null), new EnvironmentSnapshot(), DomainFixtures.Epoch);

        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ParameterValue>)run.ParametersSnapshot)["n"] = ParameterValue.From(2));
        Assert.Throws<NotSupportedException>(() => ((IList<ParameterDefinition>)experiment.Definition.ParameterDefinitions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ParameterValue>)definition.AllowedValues!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)run.ExperimentSnapshot.Task.Arguments).Add("changed"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)run.EnvironmentSnapshot.Toolchains).Add("new", "v1"));
        Assert.Throws<NotSupportedException>(() => ((IList<MetricPoint>)run.Metrics).Clear());
    }

    [Fact]
    public void RunRequiresMatchingTaskAndValidatedParameters()
    {
        var experiment = DomainFixtures.Experiment(new ParameterDefinition("n", ParameterType.Int, required: true));
        var input = new Dictionary<string, ParameterValue>();
        Assert.Equal("TaskMismatch", Assert.Throws<DomainException>(() => Run.Create(RunId.New(), experiment,
            new TaskSnapshot("wrong", "not-executed", [], TimeSpan.FromSeconds(1)), input,
            new SourceSnapshot(null, null, null), new EnvironmentSnapshot(), DomainFixtures.Epoch)).Code);
        Assert.Equal("MissingParameter", Assert.Throws<DomainException>(() => Run.Create(RunId.New(), experiment,
            DomainFixtures.Task(), input, new SourceSnapshot(null, null, null), new EnvironmentSnapshot(), DomainFixtures.Epoch)).Code);
    }

    [Fact]
    public void UnknownProvenanceStaysUnknownAndRepeatedAttemptsHaveSeparateIdentity()
    {
        var first = DomainFixtures.Run();
        var second = DomainFixtures.Run();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.SourceSnapshot.GitHeadSha);
        Assert.Null(first.SourceSnapshot.IsWorkingTreeDirty);
        Assert.Null(first.EnvironmentSnapshot.OperatingSystem);
        Assert.Empty(first.EnvironmentSnapshot.Toolchains);
    }
}
