namespace ResearchLab.Domain;

// Mutations are serialized by the owning application service. Persistence must compare
// Version atomically when saving separate loaded instances (M2); this is not a database lock.
public sealed class Run
{
    private readonly List<MetricPoint> metrics = [];
    private DateTimeOffset lastTransitionAt;
    private long? metricSequence;
    private Dictionary<string, string?>? metricUnits;
    private HashSet<string>? allowedMetricNames;
    private int metricWindowLimit;
    internal bool IsMetricWindow => metricSequence.HasValue;

    private Run()
    {
        ExperimentSnapshot = null!;
        ParametersSnapshot = null!;
        SourceSnapshot = null!;
        EnvironmentSnapshot = null!;
        Metrics = metrics.AsReadOnly();
    }

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
    public IReadOnlyList<MetricPoint> Metrics { get; }

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

    // Application must verify durable ownership loss; this is not evidence of OS process exit.
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
        if (allowedMetricNames is not null && (!allowedMetricNames.Contains(name) || metrics.Count >= metricWindowLimit))
            throw new DomainException("MetricWindowExceeded", "Metric is outside the bounded persistence window.");
        var metric = new MetricPoint(Id, name, value, step, recordedAt, unit,
            checked((metricSequence ?? metrics.Count) + 1L));
        var previous = metrics.FirstOrDefault(point => point.Name == name);
        if ((previous is not null && !string.Equals(previous.Unit, unit, StringComparison.Ordinal)) ||
            (metricUnits is not null && metricUnits.TryGetValue(name, out var existingUnit) &&
                !string.Equals(existingUnit, unit, StringComparison.Ordinal)))
            throw new DomainException("IncompatibleMetricUnit", $"Unit for metric '{name}' changed within the run.");
        metrics.Add(metric);
        if (metricSequence.HasValue)
            metricSequence++;
        Version++;
        return metric;
    }

    // Persistence-only hydration: Metrics contains new points, never a truncated history
    // masquerading as a complete aggregate. This window must use its dedicated save path.
    internal void InitializeMetricWindow(long sequence, IReadOnlyCollection<string> names,
        IReadOnlyDictionary<string, string?> units, int limit)
    {
        if (metrics.Count != 0 || metricSequence.HasValue)
            throw new InvalidOperationException("Metric window requires a freshly loaded Run without metrics.");
        metricSequence = sequence;
        metricWindowLimit = limit;
        metricUnits = new Dictionary<string, string?>(units, StringComparer.Ordinal);
        allowedMetricNames = new HashSet<string>(names, StringComparer.Ordinal);
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
            RunStatus.Created => next is RunStatus.Running or RunStatus.Failed or RunStatus.Canceled or RunStatus.Interrupted,
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
