using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class RunPreparationTests
{
    [Fact]
    public async Task PreparesFrozenDefaultsAndLiteralTaskWithoutExecutingOrPersistingRun()
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, _) = await fixture.SeedRunAsync();
        fixture.WriteConfiguration(args: ["a b", "$(command)", "; echo forbidden"]);
        var catalog = fixture.Catalog();
        var review = await catalog.ReviewTaskAsync(project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken);
        var service = new RunPreparation(catalog);
        var input = new Dictionary<string, ParameterValue>();
        var prepared = await service.PrepareAsync(experiment.Id, input, TestContext.Current.CancellationToken);
        input["n"] = ParameterValue.From(1);
        fixture.WriteConfiguration(args: ["changed"]);
        await using var db = new ResearchDbContext(new DbContextOptionsBuilder<ResearchDbContext>()
            .UseSqlite($"Data Source={fixture.Database};Pooling=False").Options);
        var stored = await db.Set<Experiment>().SingleAsync(value => value.Id == experiment.Id, TestContext.Current.CancellationToken);
        stored.Revise(new ExperimentDefinition("Changed", "", "benchmark", []), TestWorkspace.Epoch.AddDays(1));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ParameterValue.From(2), prepared.ParametersSnapshot["n"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ParameterValue>)prepared.ParametersSnapshot).Clear());
        Assert.Equal("Fixture experiment", prepared.ExperimentSnapshot.Definition.Name);
        Assert.Equal(project.Id, prepared.ExperimentSnapshot.ProjectId);
        Assert.Equal(experiment.Id, prepared.ExperimentSnapshot.ExperimentId);
        Assert.Equal(review.Fingerprint, prepared.TaskFingerprint);
        Assert.Equal(fixture.Executable, prepared.ExecutablePath);
        Assert.Equal(fixture.Workspace, prepared.WorkingDirectory);
        Assert.Equal(new[] { "a b", "$(command)", "; echo forbidden" }, prepared.ExperimentSnapshot.Task.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), prepared.ExperimentSnapshot.Task.Timeout);
        Assert.Null(prepared.SourceSnapshot.GitHeadSha);
        Assert.Null(prepared.SourceSnapshot.IsWorkingTreeDirty);
        Assert.NotEmpty(prepared.EnvironmentSnapshot.OperatingSystem!);
        Assert.NotEmpty(prepared.EnvironmentSnapshot.Architecture!);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
        Assert.Equal(1, await db.Set<Run>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() =>
            service.PrepareAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
    }

    [Theory]
    [InlineData("unknown", 1, "UnknownParameter")]
    [InlineData("n", "2", "ParameterTypeMismatch")]
    [InlineData("n", 0, "ParameterOutOfRange")]
    [InlineData("n", 3, "ParameterNotAllowed")]
    public async Task RejectsInvalidParametersBeforeTrustCheck(string name, object value, string code)
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, _) = await fixture.SeedRunAsync();
        var error = await Assert.ThrowsAsync<DomainException>(() => new RunPreparation(fixture.Catalog()).PrepareAsync(
            experiment.Id, new Dictionary<string, ParameterValue> { [name] = ParameterValue.From(value) },
            TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        Assert.Contains(name, error.Message);
    }

    [Fact]
    public async Task UsesRequiredAndOptionalRulesFromExperimentAndExplicitValuesOverrideDefaults()
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, _) = await fixture.SeedRunAsync();
        var catalog = fixture.Catalog();
        var review = await catalog.ReviewTaskAsync(project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken);
        var service = new RunPreparation(catalog);
        var prepared = await service.PrepareAsync(experiment.Id,
            new Dictionary<string, ParameterValue> { ["n"] = ParameterValue.From(1) }, TestContext.Current.CancellationToken);
        Assert.Equal(ParameterValue.From(1), prepared.ParametersSnapshot["n"]);

        var required = new Experiment(ExperimentId.New(), project.Id, "Required", "", "benchmark",
            [new ParameterDefinition("required", ParameterType.Bool, required: true),
             new ParameterDefinition("optional", ParameterType.String)], TestWorkspace.Epoch);
        await fixture.Store.AddExperimentAsync(required, TestContext.Current.CancellationToken);
        Assert.Equal("MissingParameter", (await Assert.ThrowsAsync<DomainException>(() => service.PrepareAsync(
            required.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
        var result = await service.PrepareAsync(required.Id,
            new Dictionary<string, ParameterValue> { ["required"] = ParameterValue.From(false) }, TestContext.Current.CancellationToken);
        Assert.Single(result.ParametersSnapshot);
        Assert.False(result.ParametersSnapshot.ContainsKey("optional"));
    }

    [Fact]
    public async Task RejectsMissingExperimentAndUntrustedOrChangedExecutable()
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, _) = await fixture.SeedRunAsync();
        var catalog = fixture.Catalog();
        var service = new RunPreparation(catalog);
        var input = new Dictionary<string, ParameterValue>();
        Assert.Equal("ExperimentNotFound", (await Assert.ThrowsAsync<CatalogException>(() => service.PrepareAsync(
            ExperimentId.New(), input, TestContext.Current.CancellationToken))).Code);
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() => service.PrepareAsync(
            experiment.Id, input, TestContext.Current.CancellationToken))).Code);
        var review = await catalog.ReviewTaskAsync(project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(fixture.Executable, "changed", TestContext.Current.CancellationToken);
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() => service.PrepareAsync(
            experiment.Id, input, TestContext.Current.CancellationToken))).Code);
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() => new RunPreparation(fixture.Catalog()).PrepareAsync(
            experiment.Id, input, TestContext.Current.CancellationToken))).Code);
    }

    [Theory]
    [InlineData("../outside.cmd", "UnsafePath")]
    [InlineData("missing.cmd", "PathNotFound")]
    public async Task RechecksPathsAfterConsent(string executable, string code)
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, _) = await fixture.SeedRunAsync();
        var catalog = fixture.Catalog();
        var review = await catalog.ReviewTaskAsync(project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken);
        fixture.WriteConfiguration(executable);
        Assert.Equal(code, (await Assert.ThrowsAsync<CatalogException>(() => new RunPreparation(catalog).PrepareAsync(
            experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task RejectsRemovedTaskAndHonorsCancellation()
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, _) = await fixture.SeedRunAsync();
        var catalog = fixture.Catalog();
        var review = await catalog.ReviewTaskAsync(project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken);
        var service = new RunPreparation(catalog);
        var input = new Dictionary<string, ParameterValue>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(experiment.Id, input, canceled.Token));
        await File.WriteAllTextAsync(fixture.Configuration,
            File.ReadAllText(fixture.Configuration).Replace("benchmark", "other", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        Assert.Equal("TaskNotFound", (await Assert.ThrowsAsync<CatalogException>(() =>
            service.PrepareAsync(experiment.Id, input, TestContext.Current.CancellationToken))).Code);
    }
}
