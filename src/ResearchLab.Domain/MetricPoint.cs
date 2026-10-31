namespace ResearchLab.Domain;

public sealed class MetricPoint
{
    private MetricPoint() => Name = null!;

    internal MetricPoint(RunId runId, string name, double value, long? step,
        DateTimeOffset recordedAt, string? unit, long sequence)
    {
        Name = Guard.Required(name, nameof(name));
        if (!double.IsFinite(value))
            throw new DomainException("NonFiniteMetric", "Metric values must be finite.");
        if (unit is not null)
            Guard.Required(unit, nameof(unit));
        Id = Guid.NewGuid();
        RunId = runId;
        Value = value;
        Step = step;
        RecordedAtUtc = recordedAt.ToUniversalTime();
        Unit = unit;
        Sequence = sequence;
    }

    public Guid Id { get; }
    public RunId RunId { get; }
    public string Name { get; }
    public double Value { get; }
    public long? Step { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string? Unit { get; }
    public long Sequence { get; }
}
