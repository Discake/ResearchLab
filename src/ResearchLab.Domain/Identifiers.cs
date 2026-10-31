namespace ResearchLab.Domain;

public readonly record struct ProjectId
{
    public ProjectId(Guid value) => Value = Guard.Id(value, nameof(ProjectId));
    public Guid Value { get; }
    public static ProjectId New() => new(Guid.NewGuid());
}

public readonly record struct ExperimentId
{
    public ExperimentId(Guid value) => Value = Guard.Id(value, nameof(ExperimentId));
    public Guid Value { get; }
    public static ExperimentId New() => new(Guid.NewGuid());
}

public readonly record struct RunId
{
    public RunId(Guid value) => Value = Guard.Id(value, nameof(RunId));
    public Guid Value { get; }
    public static RunId New() => new(Guid.NewGuid());
}
