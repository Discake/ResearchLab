using System.Text.Json;
using System.Text.Json.Serialization;
using ResearchLab.Domain;

namespace ResearchLab.Cli;

// Transport conversion only. Domain owns defaults, constraints and validation.
internal static class ExperimentJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyDictionary<string, ParameterValue> Parameters(string json, ExperimentDefinition definition)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Parameters must be a JSON object.");
        var values = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var parameter = definition.ParameterDefinitions.SingleOrDefault(value => value.Name == property.Name)
                ?? throw new DomainException("UnknownParameter", "Unknown experiment parameter.");
            if (!values.TryAdd(property.Name, Scalar(property.Value, parameter.Type)))
                throw new ArgumentException("Duplicate parameter names are not allowed.");
        }
        return definition.ValidateParameters(values);
    }

    public static ExperimentDefinition Definition(string json)
    {
        var input = JsonSerializer.Deserialize<DefinitionInput>(json, Options)
            ?? throw new ArgumentException("Experiment definition must be a JSON object.");
        return new ExperimentDefinition(input.Name, input.Description, input.TaskKey,
            (input.Parameters ?? []).Select(parameter =>
            {
                if (parameter is null || !Enum.TryParse<ParameterType>(parameter.Type, true, out var type) ||
                    !Enum.IsDefined(type) || int.TryParse(parameter.Type, out _))
                    throw new ArgumentException("Parameter type must be int, double, bool or string.");
                return new ParameterDefinition(parameter.Name, type, parameter.Required,
                    parameter.Default.ValueKind == JsonValueKind.Undefined ? null : Scalar(parameter.Default, type),
                    parameter.Minimum, parameter.Maximum, parameter.AllowedValues?.Select(value => Scalar(value, type)));
            }));
    }

    private static ParameterValue Scalar(JsonElement value, ParameterType type) => (type, value.ValueKind) switch
    {
        (ParameterType.Int, JsonValueKind.Number) when value.TryGetInt32(out var integer) => ParameterValue.From(integer),
        (ParameterType.Double, JsonValueKind.Number) when value.TryGetDouble(out var number) => ParameterValue.From(number),
        (ParameterType.Bool, JsonValueKind.True or JsonValueKind.False) => ParameterValue.From(value.GetBoolean()),
        (ParameterType.String, JsonValueKind.String) => ParameterValue.From(value.GetString()!),
        _ => throw new DomainException("ParameterTypeMismatch", "Parameter value does not match its declared scalar type.")
    };

    private sealed record DefinitionInput(string Name, string Description, string TaskKey, ParameterInput?[]? Parameters);
    private sealed record ParameterInput(string Name, string Type, bool Required, JsonElement Default,
        double? Minimum, double? Maximum, JsonElement[]? AllowedValues);
}
