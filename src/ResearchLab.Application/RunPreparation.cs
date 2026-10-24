using System.Runtime.InteropServices;
using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed class RunPreparation(ProjectCatalog catalog, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<PreparedRun> PrepareAsync(ExperimentId experimentId,
        IReadOnlyDictionary<string, ParameterValue> parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var experiment = await catalog.GetExperimentAsync(experimentId, cancellationToken)
            ?? throw new CatalogException("ExperimentNotFound", "Experiment was not found.");
        var project = await catalog.GetProjectAsync(experiment.ProjectId, cancellationToken);
        var definition = experiment.Definition;
        var validated = definition.ValidateParameters(parameters);
        // Reuse the session's consent and all existing configuration/path/content checks.
        var trusted = await catalog.GetTrustedTaskAsync(project.Id, definition.TaskKey, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedRun(new ExperimentSnapshot(experiment.Id, project.Id, definition, trusted.Task),
            validated, project.WorkspacePath, trusted.ExecutablePath, trusted.Fingerprint,
            new SourceSnapshot(null, null, null),
            new EnvironmentSnapshot(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString()),
            clock.GetUtcNow());
    }
}

// Preparation is neither a persisted Created Run nor permission to skip launch-time revalidation.
public sealed class PreparedRun
{
    internal PreparedRun(ExperimentSnapshot experiment, IReadOnlyDictionary<string, ParameterValue> parameters,
        string workingDirectory, string executablePath, string taskFingerprint, SourceSnapshot source,
        EnvironmentSnapshot environment, DateTimeOffset preparedAt)
    {
        ExperimentSnapshot = experiment;
        ParametersSnapshot = parameters;
        WorkingDirectory = workingDirectory;
        ExecutablePath = executablePath;
        TaskFingerprint = taskFingerprint;
        SourceSnapshot = source;
        EnvironmentSnapshot = environment;
        PreparedAtUtc = preparedAt.ToUniversalTime();
    }

    public ExperimentSnapshot ExperimentSnapshot { get; }
    public IReadOnlyDictionary<string, ParameterValue> ParametersSnapshot { get; }
    public string WorkingDirectory { get; }
    public string ExecutablePath { get; }
    public string TaskFingerprint { get; }
    public SourceSnapshot SourceSnapshot { get; }
    public EnvironmentSnapshot EnvironmentSnapshot { get; }
    public DateTimeOffset PreparedAtUtc { get; }
}
