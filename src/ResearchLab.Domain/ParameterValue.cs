namespace ResearchLab.Domain;

public enum ParameterType { Int, Double, Bool, String }

// Only immutable scalar values can enter a parameter snapshot. No implicit conversions.
public sealed record ParameterValue
{
    private ParameterValue(ParameterType type, object value)
    {
        Type = type;
        Value = value;
    }

    public ParameterType Type { get; }
    public object Value { get; }

    public static ParameterValue From(object? value) => value switch
    {
        int number => new(ParameterType.Int, number),
        double number when double.IsFinite(number) => new(ParameterType.Double, number),
        double => throw new DomainException("NonFiniteParameter", "Double parameters must be finite."),
        bool boolean => new(ParameterType.Bool, boolean),
        string text => new(ParameterType.String, text),
        _ => throw new DomainException("UnsupportedParameterType", "Parameters must be int, double, bool or string; null is not a value.")
    };

    internal double NumericValue => Type switch
    {
        ParameterType.Int => (int)Value,
        ParameterType.Double => (double)Value,
        _ => throw new DomainException("InvalidRange", "Only numeric parameters support ranges.")
    };
}
