using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class RunExecutionTests
{
    [Theory]
    [InlineData("success", RunStatus.Succeeded, 0)]
    [InlineData("fail", RunStatus.Failed, 7)]
    [InlineData("streams", RunStatus.Succeeded, 0)]
    [InlineData("parameters", RunStatus.Succeeded, 0)]
    [InlineData("literal", RunStatus.Succeeded, 0)]
    public async Task ExecutesTrustedProgramAndPersistsOutcome(string mode, RunStatus status, int exitCode)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode);
        await TrustAsync(catalog, experiment);
        var runner = new InspectingRunner(fixture, experiment.Id);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), runner);
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(status, run.Status);
        Assert.Equal(exitCode, run.ExitCode);
        Assert.Equal(2, run.Version);
        Assert.True(run.ProcessId > 0);
        Assert.NotNull(run.StartedAtUtc);
        Assert.True(run.FinishedAtUtc >= run.StartedAtUtc);
        Assert.Equal(ParameterValue.From(2), run.ParametersSnapshot["n"]);
        Assert.Empty(run.Metrics);
        Assert.Equal(status == RunStatus.Failed, run.FailureReason is not null);
        Assert.Equal(RunStatus.Running, runner.StatusAfterStart);
    }

    [Fact]
    public async Task MissingTrustOrInvalidParametersCannotReachExecutor()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new NeverRunner());
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() => service.StartRunAsync(
            experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
        await TrustAsync(catalog, experiment);
        Assert.Equal("UnknownParameter", (await Assert.ThrowsAsync<DomainException>(() => service.StartRunAsync(
            experiment.Id, new Dictionary<string, ParameterValue> { ["other"] = ParameterValue.From(1) }, TestContext.Current.CancellationToken))).Code);
        await using var db = Open(fixture);
        Assert.Empty(await db.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidExecutableFailsWithoutInventingRunningOrExitCode()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        fixture.WriteConfiguration("broken.exe");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "broken.exe"), "This is not an executable image.", TestContext.Current.CancellationToken);
        await TrustAsync(catalog, experiment);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(1, run.Version);
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.ProcessId);
        Assert.Null(run.ExitCode);
        Assert.NotEmpty(run.FailureReason!);
    }

    [Fact]
    public async Task WindowsBatchFileCannotFallBackToCommandProcessor()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        fixture.WriteConfiguration("task.cmd");
        await TrustAsync(catalog, experiment);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Null(run.ProcessId);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
    }

    [Theory]
    [InlineData("wait", RunStatus.TimedOut)]
    [InlineData("overflow", RunStatus.Failed)]
    public async Task BoundsExecutionTimeAndOutput(string mode, RunStatus expected)
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, mode, timeout: mode == "wait" ? 1 : 10);
        await TrustAsync(catalog, experiment);
        var service = new RunExecution(fixture.Store, catalog, new WorkspaceInspector(), new ProcessRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(expected, run.Status);
        Assert.NotEmpty(run.FailureReason!);
        if (expected == RunStatus.TimedOut)
            Assert.Null(run.ExitCode);
    }

    [Fact]
    public async Task ChangedBinaryAfterPreparationBlocksHandoff()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var inspector = new ChangingInspector(async () =>
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspace, ExecutableName), "changed", TestContext.Current.CancellationToken));
        var service = new RunExecution(fixture.Store, catalog, inspector, new NeverRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("TaskNotTrusted", run.FailureReason!);
        Assert.Null(run.ProcessId);
    }

    [Fact]
    public async Task FailingStartupAcknowledgementStopsOwnedProcessAndPropagatesError()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait");
        await TrustAsync(catalog, experiment);
        var prepared = await new RunPreparation(catalog).PrepareAsync(experiment.Id,
            new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        Process? owned = null;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProcessRunner().ExecuteAsync(prepared, started =>
            {
                owned = Process.GetProcessById(started.ProcessId);
                throw new InvalidOperationException("Synthetic persistence failure.");
            }));
            Assert.NotNull(owned);
            Assert.True(owned.HasExited);
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task ChangedExperimentCannotReplacePreparedDefinition()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var inspector = new ChangingInspector(async () =>
        {
            await using var db = Open(fixture);
            var stored = await db.Set<Experiment>().SingleAsync(TestContext.Current.CancellationToken);
            stored.Revise(new ExperimentDefinition("Changed", "", "benchmark", []), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        var service = new RunExecution(fixture.Store, catalog, inspector, new NeverRunner());
        Assert.Equal("PreparationChanged", (await Assert.ThrowsAsync<CatalogException>(() => service.StartRunAsync(
            experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken))).Code);
        await using var check = Open(fixture);
        Assert.Empty(await check.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewConsentForChangedTaskDoesNotAuthorizeOldPreparedLaunch()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "success");
        await TrustAsync(catalog, experiment);
        var inspector = new ChangingInspector(async () =>
        {
            fixture.WriteConfiguration(ExecutableName, ["fail"]);
            // Simulates a separate explicit owner review/approval during preparation.
            await TrustAsync(catalog, experiment);
        });
        var service = new RunExecution(fixture.Store, catalog, inspector, new NeverRunner());
        var id = await service.StartRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var run = (await fixture.Store.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("PreparationChanged", run.FailureReason!);
        Assert.Equal(new[] { "success" }, run.ExperimentSnapshot.Task.Arguments);
        Assert.Null(run.ProcessId);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "ResearchLab.ProcessFixture.exe" : "ResearchLab.ProcessFixture";

    private static async Task<(ProjectCatalog, Experiment)> SeedAsync(TestWorkspace fixture, string mode, int timeout = 10)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ProcessFixture")))
        {
            var destination = Path.Combine(fixture.Workspace, Path.GetFileName(file));
            File.Copy(file, destination);
            if (!OperatingSystem.IsWindows() && Path.GetFileName(file) == ExecutableName)
                File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }
        var args = mode == "literal" ? new[] { mode, "a b", "$(command)", "; echo forbidden", "%PATH%" } : [mode];
        fixture.WriteConfiguration(ExecutableName, args, timeout);
        var store = fixture.Store;
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var project = new Project(ProjectId.New(), "Synthetic execution project", fixture.Workspace, DateTimeOffset.UnixEpoch);
        await store.AddProjectAsync(project, TestContext.Current.CancellationToken);
        var experiment = new Experiment(ExperimentId.New(), project.Id, "Synthetic execution", "", "benchmark",
            [new ParameterDefinition("n", ParameterType.Int, defaultValue: ParameterValue.From(2))], DateTimeOffset.UnixEpoch);
        await store.AddExperimentAsync(experiment, TestContext.Current.CancellationToken);
        return (fixture.Catalog(), experiment);
    }

    private static async Task TrustAsync(ProjectCatalog catalog, Experiment experiment)
    {
        var review = await catalog.ReviewTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, review.Fingerprint, TestContext.Current.CancellationToken);
    }

    private static ResearchDbContext Open(TestWorkspace fixture) => new(new DbContextOptionsBuilder<ResearchDbContext>()
        .UseSqlite($"Data Source={fixture.Database};Pooling=False").Options);

    private sealed class NeverRunner : IProcessRunner
    {
        public Task<ProcessResult> ExecuteAsync(PreparedRun launch, Func<ProcessStarted, Task> onStarted) =>
            throw new InvalidOperationException("Executor must not be reached.");
    }

    private sealed class ChangingInspector(Func<Task> change) : IWorkspaceInspector
    {
        public async Task<WorkspaceInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            var result = await new WorkspaceInspector().InspectAsync(path, cancellationToken);
            await change();
            return result;
        }

        public Task<TaskReview> ReviewTaskAsync(Project project, string taskKey, CancellationToken cancellationToken = default) =>
            new WorkspaceInspector().ReviewTaskAsync(project, taskKey, cancellationToken);
    }

    private sealed class InspectingRunner(TestWorkspace fixture, ExperimentId experimentId) : IProcessRunner
    {
        public RunStatus? StatusAfterStart { get; private set; }

        public async Task<ProcessResult> ExecuteAsync(PreparedRun launch, Func<ProcessStarted, Task> onStarted)
        {
            await using var db = Open(fixture);
            var created = Assert.Single(await db.Set<Run>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(RunStatus.Created, created.Status);
            Assert.Null(created.ProcessId);
            return await new ProcessRunner().ExecuteAsync(launch, async started =>
            {
                await onStarted(started);
                var runs = await db.Set<Run>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
                StatusAfterStart = Assert.Single(runs).Status;
                Assert.Equal(experimentId, runs[0].ExperimentId);
            });
        }
    }
}
