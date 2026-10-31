using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed class CatalogException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IResearchStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AddProjectAsync(Project project, CancellationToken cancellationToken = default);
    Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task AddExperimentAsync(Experiment experiment, CancellationToken cancellationToken = default);
    Task<Experiment?> GetExperimentAsync(ExperimentId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Experiment>> ListExperimentsAsync(ProjectId projectId, CancellationToken cancellationToken = default);
    Task AddRunAsync(Run run, CancellationToken cancellationToken = default);
    Task<Run?> GetRunAsync(RunId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Run>> ListRunsAsync(ExperimentId experimentId, RunPageRequest page, CancellationToken cancellationToken = default);
    Task SaveRunAsync(Run run, long expectedVersion, CancellationToken cancellationToken = default);
    // Bounded append/terminal unit of work; implementations must use the same Run version fence.
    Task<IRunMetricWrite> OpenRunMetricWriteAsync(RunId id, IReadOnlyCollection<string> names,
        long? expectedVersion = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store does not support bounded metric writes.");
}

public interface IRunMetricWrite : IAsyncDisposable
{
    Run Run { get; }
    Task SaveAsync(CancellationToken cancellationToken = default);
}

public sealed record WorkspaceInspection(string CanonicalPath, IReadOnlyDictionary<string, TaskSnapshot> Tasks,
    string ConfigurationDigest, SourceSnapshot Source, IReadOnlyList<string> Diagnostics);

public sealed record TaskReview(ProjectId ProjectId, TaskSnapshot Task, string ExecutablePath, string Fingerprint);

public interface IWorkspaceInspector
{
    Task<WorkspaceInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task<TaskReview> ReviewTaskAsync(Project project, string taskKey, CancellationToken cancellationToken = default);
}

public sealed record ImportedProject(Project Project, SourceSnapshot Source, IReadOnlyList<string> Diagnostics);
