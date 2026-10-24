using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Persistence;

internal static class SnapshotJson
{
    private sealed record ValueData(ParameterType Type, string Value)
    {
        public static ValueData From(ParameterValue value) => new(value.Type,
            value.Value is double number ? number.ToString("R", CultureInfo.InvariantCulture) :
            Convert.ToString(value.Value, CultureInfo.InvariantCulture)!);

        public ParameterValue ToDomain() => ParameterValue.From(Type switch
        {
            ParameterType.Int => (object)int.Parse(Value, CultureInfo.InvariantCulture),
            ParameterType.Double => double.Parse(Value, CultureInfo.InvariantCulture),
            ParameterType.Bool => bool.Parse(Value),
            ParameterType.String => Value,
            _ => throw new JsonException("Unsupported stored parameter type.")
        });
    }

    private sealed record ParameterData(string Name, ParameterType Type, bool Required, ValueData? Default,
        double? Minimum, double? Maximum, ValueData[]? Allowed)
    {
        public static ParameterData From(ParameterDefinition value) => new(value.Name, value.Type, value.Required,
            value.DefaultValue is null ? null : ValueData.From(value.DefaultValue), value.Minimum, value.Maximum,
            value.AllowedValues?.Select(ValueData.From).ToArray());

        public ParameterDefinition ToDomain() => new(Name, Type, Required, Default?.ToDomain(), Minimum, Maximum,
            Allowed?.Select(value => value.ToDomain()));
    }

    private sealed record DefinitionData(string Name, string Description, string TaskKey, ParameterData[] Parameters)
    {
        public static DefinitionData From(ExperimentDefinition value) => new(value.Name, value.Description, value.TaskKey,
            value.ParameterDefinitions.Select(ParameterData.From).ToArray());
        public ExperimentDefinition ToDomain() => new(Name, Description, TaskKey, Parameters.Select(value => value.ToDomain()));
    }

    private sealed record TaskData(string Key, string Executable, string[] Arguments, long TimeoutTicks);
    private sealed record ExperimentData(Guid ExperimentId, Guid ProjectId, DefinitionData Definition, TaskData Task);
    private sealed record EnvironmentData(string? OperatingSystem, string? Architecture, Dictionary<string, string> Toolchains);

    public static string WriteDefinition(ExperimentDefinition value) => JsonSerializer.Serialize(DefinitionData.From(value));
    public static ExperimentDefinition ReadDefinition(string json) => Read<DefinitionData>(json).ToDomain();

    public static string WriteParameters(IReadOnlyDictionary<string, ParameterValue> values) =>
        JsonSerializer.Serialize(values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => ValueData.From(pair.Value)));
    public static IReadOnlyDictionary<string, ParameterValue> ReadParameters(string json) =>
        new ReadOnlyDictionary<string, ParameterValue>(Read<Dictionary<string, ValueData>>(json)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToDomain(), StringComparer.Ordinal));

    public static string WriteExperiment(ExperimentSnapshot value) => JsonSerializer.Serialize(new ExperimentData(
        value.ExperimentId.Value, value.ProjectId.Value, DefinitionData.From(value.Definition),
        new(value.Task.Key, value.Task.Executable, value.Task.Arguments.ToArray(), value.Task.Timeout.Ticks)));
    public static ExperimentSnapshot ReadExperiment(string json)
    {
        var data = Read<ExperimentData>(json);
        return new(new(data.ExperimentId), new(data.ProjectId), data.Definition.ToDomain(),
            new(data.Task.Key, data.Task.Executable, data.Task.Arguments, TimeSpan.FromTicks(data.Task.TimeoutTicks)));
    }

    public static string WriteSource(SourceSnapshot value) => JsonSerializer.Serialize(value);
    public static SourceSnapshot ReadSource(string json) => Read<SourceSnapshot>(json);
    public static string WriteEnvironment(EnvironmentSnapshot value) => JsonSerializer.Serialize(new EnvironmentData(
        value.OperatingSystem, value.Architecture, value.Toolchains.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary()));
    public static EnvironmentSnapshot ReadEnvironment(string json)
    {
        var data = Read<EnvironmentData>(json);
        return new(data.OperatingSystem, data.Architecture, data.Toolchains);
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new JsonException("Missing stored snapshot.");
}
