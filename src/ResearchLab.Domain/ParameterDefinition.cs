using System.Collections.ObjectModel;

namespace ResearchLab.Domain;

public sealed class ParameterDefinition
{
    public ParameterDefinition(string name, ParameterType type, bool required = false,
        ParameterValue? defaultValue = null, double? minimum = null, double? maximum = null,
        IEnumerable<ParameterValue>? allowedValues = null)
    {
        Name = Guard.Required(name, nameof(Name));
        if (!Enum.IsDefined(type))
            throw new DomainException("UnsupportedParameterType", $"Unsupported type for '{name}'.");
        Type = type;
        Required = required;
        Minimum = minimum;
        Maximum = maximum;
        if ((minimum.HasValue || maximum.HasValue) && type is not (ParameterType.Int or ParameterType.Double))
            throw new DomainException("InvalidRange", $"'{name}' is not numeric.");
        if ((minimum.HasValue && !double.IsFinite(minimum.Value)) ||
            (maximum.HasValue && !double.IsFinite(maximum.Value)) || minimum > maximum)
            throw new DomainException("InvalidRange", $"Invalid bounds for '{name}'.");

        var allowed = allowedValues?.ToArray();
        if (allowed is { Length: 0 })
            throw new DomainException("InvalidAllowedValues", $"Allowed values for '{name}' must not be empty.");
        if (allowed is not null)
            foreach (var value in allowed)
                ValidateTypeAndRange(value);
        AllowedValues = allowed is null ? null : Array.AsReadOnly(allowed);
        if (defaultValue is not null)
            Validate(defaultValue);
        DefaultValue = defaultValue;
    }

    public string Name { get; }
    public ParameterType Type { get; }
    public bool Required { get; }
    public ParameterValue? DefaultValue { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public ReadOnlyCollection<ParameterValue>? AllowedValues { get; }

    public void Validate(ParameterValue value)
    {
        ValidateTypeAndRange(value);
        if (AllowedValues is not null && !AllowedValues.Contains(value))
            throw new DomainException("ParameterNotAllowed", $"Value for '{Name}' is not allowed.");
    }

    private void ValidateTypeAndRange(ParameterValue value)
    {
        if (value is null || value.Type != Type)
            throw new DomainException("ParameterTypeMismatch", $"'{Name}' requires {Type}.");
        if (Type is ParameterType.Int or ParameterType.Double &&
            (value.NumericValue < Minimum || value.NumericValue > Maximum))
            throw new DomainException("ParameterOutOfRange", $"'{Name}' is outside its inclusive bounds.");
    }
}
