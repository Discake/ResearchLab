namespace ResearchLab.Domain;

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class Guard
{
    public static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("Required", $"{field} must not be blank.");
        return value;
    }

    public static Guid Id(Guid value, string field)
    {
        if (value == Guid.Empty)
            throw new DomainException("InvalidId", $"{field} must not be empty.");
        return value;
    }
}
