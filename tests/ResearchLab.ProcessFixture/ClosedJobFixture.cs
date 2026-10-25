using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;
using static ResearchLab.Execution.WindowsNativeMethods;

// An intentionally unrecoverable owner lives in its own test process. OS exit releases its
// remaining logs; the production owner must never claim group completion from a lost Job.
internal static class ClosedJobFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var store = new SqliteResearchStore(args[1]);
        var id = new ExperimentId(Guid.Parse(args[2]));
        var experiment = (await store.GetExperimentAsync(id))!;
        var catalog = new ProjectCatalog(store, new WorkspaceInspector());
        var review = await catalog.ReviewTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey);
        await catalog.ConfirmTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, review.Fingerprint);
        var runner = new ProcessRunner(new RunLogFiles(args[3])) { JobControl = new ClosingJobControl() };
        var operation = await new RunExecution(store, catalog, new WorkspaceInspector(), runner)
            .PrepareRunAsync(id, new Dictionary<string, ParameterValue>());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await operation.ExecuteAsync(); return 21; }
            catch (ProcessCleanupException error)
            {
                if (!ReferenceEquals(error.Operation, operation)) return 22;
            }
        }
        var run = (await store.GetRunAsync(operation.RunId))!;
        Console.WriteLine(JsonSerializer.Serialize(new { Id = run.Id.Value, Status = run.Status.ToString(), run.Version }));
        return run.Status == RunStatus.Running && run.Version == 1 ? 0 : 23;
    }

    private sealed class ClosingJobControl : IWindowsJobControl
    {
        private readonly WindowsJobControl real = new();
        public uint ActiveProcesses(KernelHandle job)
        {
            job.Dispose(); // Actually close the only Job handle before any zero-count proof.
            return real.ActiveProcesses(job);
        }
        public void Terminate(KernelHandle job) => real.Terminate(job);
    }
}
