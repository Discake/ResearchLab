using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task ConcurrentWritersSaveExactlyOneCompletionAndItsMetrics()
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        run.Start(TestWorkspace.Epoch, 1, run.Version);
        await fixture.Store.SaveRunAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        var second = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        first.AddMetric("winner", 1, TestWorkspace.Epoch, first.Version);
        second.AddMetric("winner", 2, TestWorkspace.Epoch, second.Version);
        first.Succeed(TestWorkspace.Epoch, 0, true, first.Version);
        second.Fail(TestWorkspace.Epoch, "fixture failure", second.Version, 1);
        using var start = new ManualResetEventSlim();
        async Task<bool> Save(Run value)
        {
            start.Wait(TestContext.Current.CancellationToken);
            try { await fixture.Store.SaveRunAsync(value, 1, TestContext.Current.CancellationToken); return true; }
            catch (CatalogException exception) when (exception.Code == "VersionConflict") { return false; }
        }
        var one = Task.Run(() => Save(first), TestContext.Current.CancellationToken);
        var two = Task.Run(() => Save(second), TestContext.Current.CancellationToken);
        start.Set();
        var outcomes = await Task.WhenAll(one, two);
        Assert.Single(outcomes, success => success);
        var loaded = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(outcomes[0] ? RunStatus.Succeeded : RunStatus.Failed, loaded.Status);
        Assert.Equal(outcomes[0] ? 1 : 2, Assert.Single(loaded.Metrics).Value);
    }

    [Fact]
    public async Task MigrationsAreIdempotentAndCatalogSurvivesReopening()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        var catalog = fixture.Catalog();
        var imported = await catalog.ImportProjectAsync(fixture.Workspace, "Imported fixture", TestContext.Current.CancellationToken);
        var experiment = await catalog.CreateExperimentAsync(imported.Project.Id, new("Experiment", "Fixture", "benchmark",
            [new ParameterDefinition("tolerance", ParameterType.Double, defaultValue: ParameterValue.From(0.01)),
             new ParameterDefinition("enabled", ParameterType.Bool, defaultValue: ParameterValue.From(true)),
             new ParameterDefinition("label", ParameterType.String, defaultValue: ParameterValue.From("fixture"))]), TestContext.Current.CancellationToken);

        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        var reopened = fixture.Catalog();
        Assert.Equal(imported.Project.Id, Assert.Single(await reopened.ListProjectsAsync(TestContext.Current.CancellationToken)).Id);
        Assert.Equal(fixture.Workspace, (await reopened.GetProjectAsync(imported.Project.Id, TestContext.Current.CancellationToken)).WorkspacePath);
        Assert.Equal(experiment.Id, Assert.Single(await reopened.ListExperimentsAsync(imported.Project.Id, TestContext.Current.CancellationToken)).Id);
        var loaded = Assert.IsType<Experiment>(await reopened.GetExperimentAsync(experiment.Id, TestContext.Current.CancellationToken));
        Assert.Equal(experiment.CreatedAtUtc, loaded.CreatedAtUtc);
        var values = loaded.Definition.ValidateParameters(new Dictionary<string, ParameterValue>());
        Assert.Equal(ParameterValue.From(0.01), values["tolerance"]);
        Assert.Equal(ParameterValue.From(true), values["enabled"]);
        Assert.Equal(ParameterValue.From("fixture"), values["label"]);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        Assert.Equal(3L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunRoundTripPreservesSnapshotsMetricIdentityAndLifecycle()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, created) = await fixture.SeedRunAsync();
        var running = Assert.IsType<Run>(await fixture.Store.GetRunAsync(created.Id, TestContext.Current.CancellationToken));
        running.Start(TestWorkspace.Epoch.AddSeconds(1), 123, running.Version);
        var point = running.AddMetric("residual", 0.1, TestWorkspace.Epoch.AddSeconds(2), running.Version, 1, "1");
        await fixture.Store.SaveRunAsync(running, 0, TestContext.Current.CancellationToken);

        var reopened = Assert.IsType<Run>(await fixture.Store.GetRunAsync(created.Id, TestContext.Current.CancellationToken));
        Assert.Equal(RunStatus.Running, reopened.Status);
        Assert.Equal(2, reopened.Version);
        Assert.Equal(experiment.Id, reopened.ExperimentId);
        Assert.Equal(point.Id, Assert.Single(reopened.Metrics).Id);
        Assert.Equal(ParameterValue.From(2), reopened.ParametersSnapshot["n"]);
        Assert.Equal("--fixture", Assert.Single(reopened.ExperimentSnapshot.Task.Arguments));
        Assert.Equal("fixture-version", reopened.EnvironmentSnapshot.Toolchains["compiler"]);
        Assert.True(reopened.SourceSnapshot.IsWorkingTreeDirty);
        Assert.Equal("fixture-sha", reopened.SourceSnapshot.GitHeadSha);
        Assert.Equal("ParameterNotAllowed", Assert.Throws<DomainException>(() =>
            reopened.ExperimentSnapshot.Definition.ParameterDefinitions[0].Validate(ParameterValue.From(3))).Code);
        Assert.Equal("IncompatibleMetricUnit", Assert.Throws<DomainException>(() =>
            reopened.AddMetric("residual", 1, TestWorkspace.Epoch, reopened.Version, unit: "ms")).Code);
        reopened.AddMetric("residual", 0.05, point.RecordedAtUtc, reopened.Version, point.Step, "1");
        reopened.Fail(TestWorkspace.Epoch.AddSeconds(3), "Fixture failure", reopened.Version, 2);
        await fixture.Store.SaveRunAsync(reopened, 2, TestContext.Current.CancellationToken);

        var terminal = Assert.IsType<Run>(await fixture.Store.GetRunAsync(created.Id, TestContext.Current.CancellationToken));
        Assert.Equal(RunStatus.Failed, terminal.Status);
        Assert.Equal(2, terminal.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(2), terminal.Duration);
        Assert.Equal(new long[] { 1, 2 }, terminal.Metrics.Select(metric => metric.Sequence));
        Assert.Throws<DomainException>(() => terminal.Start(TestWorkspace.Epoch.AddSeconds(4), 1, terminal.Version));
        Assert.Equal("ImmutableRun", (await Assert.ThrowsAsync<CatalogException>(() => fixture.Store.SaveRunAsync(terminal, terminal.Version, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task StaleWriterCannotOverwriteCompletionOrAppendMetrics()
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        run.Start(TestWorkspace.Epoch, 1, run.Version);
        await fixture.Store.SaveRunAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        var second = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        first.Cancel(TestWorkspace.Epoch.AddSeconds(1), first.Version);
        second.AddMetric("discarded", 1, TestWorkspace.Epoch, second.Version);
        second.Succeed(TestWorkspace.Epoch.AddSeconds(1), 0, true, second.Version);
        await fixture.Store.SaveRunAsync(first, 1, TestContext.Current.CancellationToken);
        Assert.Equal("VersionConflict", (await Assert.ThrowsAsync<CatalogException>(() => fixture.Store.SaveRunAsync(second, 1, TestContext.Current.CancellationToken))).Code);
        var actual = (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Canceled, actual.Status);
        Assert.Empty(actual.Metrics);
    }

    [Fact]
    public async Task CannotReplacePersistedSnapshotWithAnotherRunUsingSameId()
    {
        using var fixture = new TestWorkspace();
        var (_, experiment, run) = await fixture.SeedRunAsync();
        var replacement = Run.Create(run.Id, experiment, run.ExperimentSnapshot.Task,
            new Dictionary<string, ParameterValue> { ["n"] = ParameterValue.From(1) }, run.SourceSnapshot, run.EnvironmentSnapshot, run.CreatedAtUtc);
        replacement.Start(TestWorkspace.Epoch, 1, replacement.Version);
        Assert.Equal("ImmutableSnapshot", (await Assert.ThrowsAsync<CatalogException>(() => fixture.Store.SaveRunAsync(replacement, 0, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(ParameterValue.From(2), (await fixture.Store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.ParametersSnapshot["n"]);
    }

    [Fact]
    public async Task RejectsDuplicateWorkspaceAndMissingParent()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        var catalog = fixture.Catalog();
        await catalog.ImportProjectAsync(fixture.Workspace, "First", TestContext.Current.CancellationToken);
        Assert.Equal("ProjectAlreadyImported", (await Assert.ThrowsAsync<CatalogException>(() =>
            catalog.ImportProjectAsync(Path.Combine(fixture.Workspace, "."), "Duplicate", TestContext.Current.CancellationToken))).Code);
        var orphan = new Experiment(ExperimentId.New(), ProjectId.New(), "Orphan", "", "benchmark", [], TestWorkspace.Epoch);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Store.AddExperimentAsync(orphan, TestContext.Current.CancellationToken));
        Assert.Single(await catalog.ListProjectsAsync(TestContext.Current.CancellationToken));
    }
}
