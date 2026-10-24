using System.Runtime.ExceptionServices;
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
        var operation = await PrepareRunAsync(experimentId, parameters, cancellationToken);
        return await operation.ExecuteAsync();
    }

    // Persists and prepares an owned attempt, but does not start an OS process.
    public async Task<RunOperation> PrepareRunAsync(ExperimentId experimentId,
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
        var persistedVersion = run.Version;
        ExceptionDispatchInfo? callbackFailure = null;
        var trustChecked = false;
        var process = runner.CreateExecution(run.Id, prepared);
        return new RunOperation(run.Id, ExecuteOwnedAsync, process);

        async Task<RunId> ExecuteOwnedAsync()
        {
            if (!trustChecked)
            {
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    run.Fail(clock.GetUtcNow(), "Launch-time trust verification could not read the declared task.", run.Version);
                    await store.SaveRunAsync(run, 0);
                    return run.Id;
                }
                trustChecked = true;
            }
            ProcessResult result;
            try
            {
                result = await process.ExecuteAsync(async started =>
                {
                    try
                    {
                        run.Start(started.StartedAtUtc, started.ProcessId, run.Version);
                        await store.SaveRunAsync(run, persistedVersion);
                        persistedVersion = run.Version;
                    }
                    catch (Exception exception)
                    {
                        callbackFailure = ExceptionDispatchInfo.Capture(exception);
                        throw;
                    }
                });
                // An adapter must not swallow a failed acknowledgement and report success.
                callbackFailure?.Throw();
            }
            catch (ProcessCleanupException)
            {
                // Process may still be alive. Keep the owner and retry cleanup explicitly.
                throw;
            }
            catch (Exception) when (callbackFailure is not null)
            {
                // Once cleanup is confirmed, preserve the original acknowledgement error.
                callbackFailure.Throw();
                throw;
            }
            catch (Exception)
            {
                // Execution failures are distinct from callback/terminal persistence failures.
                // Exception text may contain private paths or environment data.
                result = new ProcessResult(null, false, "Process execution or result collection failed.");
            }
            var finishedAt = clock.GetUtcNow();
            if (result.Canceled && result.FailureReason is null)
                run.Cancel(finishedAt, run.Version);
            else if (run.Status == RunStatus.Created)
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
