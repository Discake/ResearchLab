using System.Collections.Concurrent;
using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed class ProjectCatalog(IResearchStore store, IWorkspaceInspector inspector, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(ProjectId, string), string> consents = new();

    public async Task<ImportedProject> ImportProjectAsync(string path, string name, CancellationToken cancellationToken = default)
    {
        var inspection = await inspector.InspectAsync(path, cancellationToken);
        var project = new Project(ProjectId.New(), name, inspection.CanonicalPath, clock.GetUtcNow(), gitRemoteUrl: inspection.Source.RepositoryUrl);
        await store.AddProjectAsync(project, cancellationToken);
        return new ImportedProject(project, inspection.Source, inspection.Diagnostics);
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default) => store.ListProjectsAsync(cancellationToken);

    public async Task<Project> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default) =>
        await store.GetProjectAsync(id, cancellationToken) ?? throw new CatalogException("ProjectNotFound", "Project was not found.");

    public async Task<Experiment> CreateExperimentAsync(ProjectId projectId, ExperimentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var inspection = await inspector.InspectAsync(project.WorkspacePath, cancellationToken);
        if (!inspection.Tasks.ContainsKey(definition.TaskKey))
            throw new CatalogException("TaskNotFound", "The experiment task is not declared in researchlab.json.");
        var experiment = new Experiment(ExperimentId.New(), projectId, definition.Name, definition.Description,
            definition.TaskKey, definition.ParameterDefinitions, clock.GetUtcNow());
        await store.AddExperimentAsync(experiment, cancellationToken);
        return experiment;
    }

    public Task<Experiment?> GetExperimentAsync(ExperimentId id, CancellationToken cancellationToken = default) => store.GetExperimentAsync(id, cancellationToken);

    public Task<IReadOnlyList<Experiment>> ListExperimentsAsync(ProjectId projectId, CancellationToken cancellationToken = default) =>
        store.ListExperimentsAsync(projectId, cancellationToken);

    public async Task<TaskReview> ReviewTaskAsync(ProjectId projectId, string taskKey, CancellationToken cancellationToken = default) =>
        await inspector.ReviewTaskAsync(await GetProjectAsync(projectId, cancellationToken), taskKey, cancellationToken);

    // Call only after the local owner explicitly approves the displayed review.
    // Consent is deliberately session-local and never inferred from an import.
    public async Task ConfirmTaskAsync(ProjectId projectId, string taskKey, string reviewedFingerprint, CancellationToken cancellationToken = default)
    {
        var current = await ReviewTaskAsync(projectId, taskKey, cancellationToken);
        if (!string.Equals(current.Fingerprint, reviewedFingerprint, StringComparison.Ordinal))
            throw new CatalogException("TaskChanged", "The task or executable changed after review; review it again.");
        consents[(projectId, taskKey)] = current.Fingerprint;
    }

    public async Task<TaskReview> GetTrustedTaskAsync(ProjectId projectId, string taskKey, CancellationToken cancellationToken = default)
    {
        var current = await ReviewTaskAsync(projectId, taskKey, cancellationToken);
        if (!consents.TryGetValue((projectId, taskKey), out var fingerprint) || current.Fingerprint != fingerprint)
            throw new CatalogException("TaskNotTrusted", "Explicit confirmation of the current task is required.");
        return current;
    }
}
