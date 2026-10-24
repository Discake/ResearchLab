using Xunit;

namespace ResearchLab.Domain.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void IdentityUsesExplicitGuidRatherThanWorkspacePath()
    {
        var id = ProjectId.New();
        var first = new Project(id, "Test project", "fixture-path-a", DomainFixtures.Epoch);
        var moved = new Project(id, "Test project", "fixture-path-b", DomainFixtures.Epoch);
        Assert.Equal(first.Id, moved.Id);
        Assert.NotEqual(first.WorkspacePath, moved.WorkspacePath);
        Assert.Equal(id, new ProjectId(id.Value));
        Assert.NotEqual(id, ProjectId.New());
    }

    [Fact]
    public void EmptyAndDefaultIdsCannotEnterAggregates()
    {
        Assert.Throws<DomainException>(() => new ProjectId(Guid.Empty));
        Assert.Throws<DomainException>(() => new ExperimentId(Guid.Empty));
        Assert.Throws<DomainException>(() => new RunId(Guid.Empty));
        Assert.Throws<DomainException>(() => new Project(default, "name", "path", DomainFixtures.Epoch));
        Assert.Throws<DomainException>(() => new Experiment(default, ProjectId.New(), "name", "", "task", [], DomainFixtures.Epoch));
        Assert.Throws<DomainException>(() => new Experiment(ExperimentId.New(), default, "name", "", "task", [], DomainFixtures.Epoch));
        Assert.Throws<DomainException>(() => Run.Create(default, DomainFixtures.Experiment(), DomainFixtures.Task(),
            new Dictionary<string, ParameterValue>(), new SourceSnapshot(null, null, null), new EnvironmentSnapshot(), DomainFixtures.Epoch));
    }

    [Fact]
    public void RejectsInvalidMetadataAndBackdatedRevisions()
    {
        Assert.Throws<DomainException>(() => new Project(ProjectId.New(), " ", "path", DomainFixtures.Epoch));
        Assert.Throws<DomainException>(() => new Project(ProjectId.New(), "name", " ", DomainFixtures.Epoch));
        Assert.Throws<DomainException>(() => new TaskSnapshot("task", " ", [], TimeSpan.FromSeconds(1)));
        Assert.Throws<DomainException>(() => new TaskSnapshot("task", "program", [], TimeSpan.Zero));
        Assert.Throws<DomainException>(() => new TaskSnapshot("task", "program", [null!], TimeSpan.FromSeconds(1)));
        var experiment = DomainFixtures.Experiment();
        var original = experiment.Definition;
        Assert.Throws<DomainException>(() => experiment.Revise(new("new", "", "task", []), DomainFixtures.Epoch.AddSeconds(-1)));
        Assert.Same(original, experiment.Definition);
        Assert.Throws<DomainException>(() => Run.Create(RunId.New(), experiment, DomainFixtures.Task(),
            new Dictionary<string, ParameterValue>(), new SourceSnapshot(null, null, null), new EnvironmentSnapshot(), DomainFixtures.Epoch.AddSeconds(-1)));
    }
}
