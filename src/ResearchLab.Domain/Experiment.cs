using System.Collections.ObjectModel;

namespace ResearchLab.Domain;

public sealed class Experiment
{
    private Experiment() => Definition = null!;

    public Experiment(ExperimentId id, ProjectId projectId, string name, string description,
        string taskKey, IEnumerable<ParameterDefinition> parameterDefinitions, DateTimeOffset createdAt)
    {
        Guard.Id(id.Value, nameof(id));
        Guard.Id(projectId.Value, nameof(projectId));
        Id = id;
        ProjectId = projectId;
        Definition = new ExperimentDefinition(name, description, taskKey, parameterDefinitions);
        CreatedAtUtc = UpdatedAtUtc = createdAt.ToUniversalTime();
    }

    public ExperimentId Id { get; }
    public ProjectId ProjectId { get; }
    public ExperimentDefinition Definition { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Revise(ExperimentDefinition definition, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (updatedAt < UpdatedAtUtc)
            throw new DomainException("InvalidTimestamp", "Experiment update predates its previous revision.");
        Definition = definition;
        UpdatedAtUtc = updatedAt.ToUniversalTime();
    }
}

// Shared safely by an Experiment and earlier Run snapshots: every member is immutable.
public sealed class ExperimentDefinition
{
    public ExperimentDefinition(string name, string description, string taskKey,
        IEnumerable<ParameterDefinition> parameterDefinitions)
    {
        Name = Guard.Required(name, nameof(name));
        ArgumentNullException.ThrowIfNull(description);
        Description = description;
        TaskKey = Guard.Required(taskKey, nameof(taskKey));
        ArgumentNullException.ThrowIfNull(parameterDefinitions);
        var definitions = parameterDefinitions.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition is null)
                throw new DomainException("InvalidParameterDefinition", "Parameter definitions must not be null.");
            if (!names.Add(definition.Name))
                throw new DomainException("DuplicateParameter", $"Duplicate parameter '{definition.Name}'.");
        }
        ParameterDefinitions = Array.AsReadOnly(definitions);
    }

    public string Name { get; }
    public string Description { get; }
    public string TaskKey { get; }
    public ReadOnlyCollection<ParameterDefinition> ParameterDefinitions { get; }

    public IReadOnlyDictionary<string, ParameterValue> ValidateParameters(IReadOnlyDictionary<string, ParameterValue> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // Copy with an ordinal comparer, irrespective of the caller's dictionary comparer.
        var supplied = new Dictionary<string, ParameterValue>(input, StringComparer.Ordinal);
        var result = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var definition in ParameterDefinitions)
        {
            if (supplied.Remove(definition.Name, out var value))
            {
                definition.Validate(value);
                result.Add(definition.Name, value);
            }
            else if (definition.DefaultValue is not null)
                result.Add(definition.Name, definition.DefaultValue);
            else if (definition.Required)
                throw new DomainException("MissingParameter", $"Required parameter '{definition.Name}' is missing.");
        }
        if (supplied.Count != 0)
            throw new DomainException("UnknownParameter", $"Unknown parameter '{supplied.Keys.First()}'.");
        return new ReadOnlyDictionary<string, ParameterValue>(result);
    }
}
