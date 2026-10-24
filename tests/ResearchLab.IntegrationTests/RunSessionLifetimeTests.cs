using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed partial class RunExecutionTests
{
    [Fact]
    public async Task DisposingUnstartedOperationEndsItsSessionAndPreventsSubsequentLaunch()
    {
        using var fixture = new TestWorkspace();
        var (catalog, experiment) = await SeedAsync(fixture, "wait");
        await TrustAsync(catalog, experiment);
        var operation = await new RunExecution(fixture.Store, catalog, new WorkspaceInspector(),
                new ProcessRunner(new RunLogFiles(Path.Combine(fixture.Root, "logs"))))
            .PrepareRunAsync(experiment.Id, new Dictionary<string, ParameterValue>(), TestContext.Current.CancellationToken);
        var whileOwned = await new RunRecovery(fixture.Store).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Empty(whileOwned.Recovered);
        Assert.Single(whileOwned.UnverifiedSessions);
        operation.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.ExecuteAsync());
        var ended = await new RunRecovery(fixture.Store).RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(operation.RunId, Assert.Single(ended.Recovered));
        var run = (await fixture.Store.GetRunAsync(operation.RunId, TestContext.Current.CancellationToken))!;
        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Null(run.ProcessId);
        Assert.Null(run.StartedAtUtc);
    }
}
