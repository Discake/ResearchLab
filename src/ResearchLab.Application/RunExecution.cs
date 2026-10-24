using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed class RunExecution(IResearchStore store, ProjectCatalog catalog, IWorkspaceInspector inspector,
    IProcessRunner runner, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // Completes when the terminal state is saved. No detached work or automatic retries.
    public async Task<RunId> StartRunAsync(ExperimentId experimentId,
        IReadOnlyDictionary<string, ParameterValue> parameters, CancellationToken cancellationToken = default)
    {
        var prepared = await new RunPreparation(catalog, clock).PrepareAsync(experimentId, parameters, cancellationToken);
        var inspection = await inspector.InspectAsync(prepared.WorkingDirectory, cancellationToken);
        var experiment = await catalog.GetExperimentAsync(experimentId, cancellationToken)
            ?? throw new CatalogException("ExperimentNotFound", "Experiment was not found.");
        var project = await catalog.GetProjectAsync(experiment.ProjectId, cancellationToken);
        if (project.Id != prepared.ExperimentSnapshot.ProjectId || project.WorkspacePath != prepared.WorkingDirectory ||
            inspection.CanonicalPath != prepared.WorkingDirectory || !SameDefinition(experiment.Definition, prepared.ExperimentSnapshot.Definition))
            throw new CatalogException("PreparationChanged", "The experiment or workspace changed during preparation; prepare again.");

        var run = Run.Create(RunId.New(), experiment, prepared.ExperimentSnapshot.Task, prepared.ParametersSnapshot,
            inspection.Source, prepared.EnvironmentSnapshot, clock.GetUtcNow());
        await store.AddRunAsync(run, cancellationToken);
        // Once persisted, request cancellation must not abandon process ownership/status writes.
        try
        {
            var trusted = await catalog.GetTrustedTaskAsync(project.Id, experiment.Definition.TaskKey);
            if (trusted.Fingerprint != prepared.TaskFingerprint || trusted.ExecutablePath != prepared.ExecutablePath)
                throw new CatalogException("PreparationChanged", "The trusted task changed during preparation; prepare again.");
        }
        catch (CatalogException exception)
        {
            run.Fail(clock.GetUtcNow(), $"{exception.Code}: {exception.Message}", run.Version);
            await store.SaveRunAsync(run, 0);
            return run.Id;
        }

        var persistedVersion = run.Version;
        var result = await runner.ExecuteAsync(prepared, async started =>
        {
            run.Start(started.StartedAtUtc, started.ProcessId, run.Version);
            await store.SaveRunAsync(run, persistedVersion);
            persistedVersion = run.Version;
        });
        var finishedAt = clock.GetUtcNow();
        if (run.Status == RunStatus.Created)
            run.Fail(finishedAt, result.FailureReason ?? "Process startup failed.", run.Version);
        else if (result.TimedOut)
            run.TimeOut(finishedAt, run.Version);
        else if (result.FailureReason is not null || result.ExitCode != 0)
            run.Fail(finishedAt, result.FailureReason ?? $"Process exited with code {result.ExitCode}.", run.Version, result.ExitCode);
        else
            // This increment requires successful stream draining, but has no metric/artifact protocol.
            run.Succeed(finishedAt, 0, resultsValidated: true, run.Version);
        await store.SaveRunAsync(run, persistedVersion);
        return run.Id;
    }

    private static bool SameDefinition(ExperimentDefinition left, ExperimentDefinition right) =>
        left.Name == right.Name && left.Description == right.Description && left.TaskKey == right.TaskKey &&
        left.ParameterDefinitions.Count == right.ParameterDefinitions.Count &&
        left.ParameterDefinitions.Zip(right.ParameterDefinitions).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Type == pair.Second.Type &&
            pair.First.Required == pair.Second.Required && pair.First.DefaultValue == pair.Second.DefaultValue &&
            pair.First.Minimum == pair.Second.Minimum && pair.First.Maximum == pair.Second.Maximum &&
            (pair.First.AllowedValues is null ? pair.Second.AllowedValues is null :
                pair.Second.AllowedValues is not null && pair.First.AllowedValues.SequenceEqual(pair.Second.AllowedValues)));
}
