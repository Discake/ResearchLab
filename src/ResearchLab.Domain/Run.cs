using System.Collections.ObjectModel;

namespace ResearchLab.Domain;

// Mutations are serialized by the owning application service. Persistence must compare
// Version atomically when saving separate loaded instances (M2); this is not a database lock.
public sealed class Run
{
    private readonly List<MetricPoint> metrics = [];
    private readonly Dictionary<string, string?> metricUnits = new(StringComparer.Ordinal);
    private DateTimeOffset lastTransitionAt;

    private Run(RunId id, ExperimentSnapshot experiment, IReadOnlyDictionary<string, ParameterValue> parameters,
        SourceSnapshot source, EnvironmentSnapshot environment, DateTimeOffset createdAt)
    {
        Id = id;
        ExperimentSnapshot = experiment;
        ParametersSnapshot = parameters;
        SourceSnapshot = source;
        EnvironmentSnapshot = environment;
        CreatedAtUtc = lastTransitionAt = createdAt.ToUniversalTime();
        Metrics = metrics.AsReadOnly();
    }

    public static Run Create(RunId id, Experiment experiment, TaskSnapshot task,
        IReadOnlyDictionary<string, ParameterValue> parameters, SourceSnapshot source,
        EnvironmentSnapshot environment, DateTimeOffset createdAt)
    {
        Guard.Id(id.Value, nameof(id));
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(environment);
        var definition = experiment.Definition;
        if (!string.Equals(definition.TaskKey, task.Key, StringComparison.Ordinal))
            throw new DomainException("TaskMismatch", "The supplied task does not match the experiment TaskKey.");
        if (createdAt < experiment.UpdatedAtUtc)
            throw new DomainException("InvalidTimestamp", "Run creation predates the experiment revision.");
        var validated = definition.ValidateParameters(parameters);
        var snapshot = new ExperimentSnapshot(experiment.Id, experiment.ProjectId, definition, task);
        return new Run(id, snapshot, validated, source, environment, createdAt);
    }

    public RunId Id { get; }
    public ExperimentId ExperimentId => ExperimentSnapshot.ExperimentId;
    public ExperimentSnapshot ExperimentSnapshot { get; }
    public IReadOnlyDictionary<string, ParameterValue> ParametersSnapshot { get; }
    public SourceSnapshot SourceSnapshot { get; }
    public EnvironmentSnapshot EnvironmentSnapshot { get; }
    public RunStatus Status { get; private set; } = RunStatus.Created;
    public long Version { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    public int? ProcessId { get; private set; }
    public int? ExitCode { get; private set; }
    public string? FailureReason { get; private set; }
    public TimeSpan Timeout => ExperimentSnapshot.Task.Timeout;
    public TimeSpan? Duration => FinishedAtUtc - StartedAtUtc;
    public ReadOnlyCollection<MetricPoint> Metrics { get; }

    // The caller must have successfully started the OS process before invoking this method.
    public void Start(DateTimeOffset at, int processId, long expectedVersion)
    {
        CheckTransition(RunStatus.Running, at, expectedVersion);
        if (processId <= 0)
            throw new DomainException("InvalidProcessId", "A started process needs a positive process ID.");
        StartedAtUtc = at.ToUniversalTime();
        ProcessId = processId;
        ApplyTransition(RunStatus.Running, at);
    }

    public void Succeed(DateTimeOffset at, int exitCode, bool resultsValidated, long expectedVersion)
    {
        CheckTransition(RunStatus.Succeeded, at, expectedVersion);
        if (exitCode != 0 || !resultsValidated)
            throw new DomainException("InvalidSuccess", "Success requires exit code zero and completed result validation.");
        Finish(RunStatus.Succeeded, at, exitCode, null);
    }

    public void Fail(DateTimeOffset at, string reason, long expectedVersion, int? exitCode = null)
    {
        CheckTransition(RunStatus.Failed, at, expectedVersion);
        Guard.Required(reason, nameof(reason));
        if (Status == RunStatus.Created && exitCode.HasValue)
            throw new DomainException("InvalidExitCode", "A process that never started has no exit code.");
        Finish(RunStatus.Failed, at, exitCode, reason);
    }

    public void Cancel(DateTimeOffset at, long expectedVersion)
    {
        CheckTransition(RunStatus.Canceled, at, expectedVersion);
        Finish(RunStatus.Canceled, at, null, null);
    }

    public void TimeOut(DateTimeOffset at, long expectedVersion)
    {
        CheckTransition(RunStatus.TimedOut, at, expectedVersion);
        if (at - StartedAtUtc < Timeout)
            throw new DomainException("TimeoutNotReached", "The configured timeout has not elapsed.");
        Finish(RunStatus.TimedOut, at, null, "Configured execution timeout exceeded.");
    }

    public void Interrupt(DateTimeOffset at, string reason, long expectedVersion)
    {
        CheckTransition(RunStatus.Interrupted, at, expectedVersion);
        Guard.Required(reason, nameof(reason));
        Finish(RunStatus.Interrupted, at, null, reason);
    }

    // Drain and validate pending metrics before recording the terminal state.
    public MetricPoint AddMetric(string name, double value, DateTimeOffset recordedAt,
        long expectedVersion, long? step = null, string? unit = null)
    {
        CheckVersion(expectedVersion);
        if (Status != RunStatus.Running)
            throw new DomainException("InvalidMetricState", "Metrics can only be appended while Running.");
        var metric = new MetricPoint(Id, name, value, step, recordedAt, unit, metrics.Count + 1L);
        if (metricUnits.TryGetValue(name, out var previousUnit) &&
            !string.Equals(previousUnit, unit, StringComparison.Ordinal))
            throw new DomainException("IncompatibleMetricUnit", $"Unit for metric '{name}' changed within the run.");
        metricUnits[name] = unit;
        metrics.Add(metric);
        Version++;
        return metric;
    }

    private void CheckVersion(long expectedVersion)
    {
        if (Version != expectedVersion)
            throw new DomainException("VersionConflict", $"Expected Run version {expectedVersion}, current version is {Version}.");
    }

    private void CheckTransition(RunStatus next, DateTimeOffset at, long expectedVersion)
    {
        CheckVersion(expectedVersion);
        var allowed = Status switch
        {
            RunStatus.Created => next is RunStatus.Running or RunStatus.Failed or RunStatus.Canceled,
            RunStatus.Running => next is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Canceled
                or RunStatus.TimedOut or RunStatus.Interrupted,
            _ => false
        };
        if (!allowed)
            throw new DomainException("InvalidRunTransition", $"Run cannot transition from {Status} to {next}.");
        if (at < lastTransitionAt)
            throw new DomainException("InvalidTimestamp", "Run transition predates the previous transition.");
    }

    private void Finish(RunStatus status, DateTimeOffset at, int? exitCode, string? reason)
    {
        FinishedAtUtc = at.ToUniversalTime();
        ExitCode = exitCode;
        FailureReason = reason;
        ApplyTransition(status, at);
    }

    private void ApplyTransition(RunStatus status, DateTimeOffset at)
    {
        Status = status;
        lastTransitionAt = at.ToUniversalTime();
        Version++;
    }
}
