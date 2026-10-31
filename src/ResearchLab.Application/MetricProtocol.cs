using System.Text;
using System.Text.Json;

namespace ResearchLab.Application;

public sealed record MetricEvent(string Name, double Value, long? Step, string? Unit);

public enum MetricProtocolError
{
    EventTooLarge,
    InvalidUtf8,
    InvalidJson,
    InvalidEnvelope,
    MissingField,
    InvalidFieldType,
    InvalidName,
    InvalidUnit,
    InvalidValue,
    InvalidStep,
    UnknownField,
    DuplicateField,
    UnsupportedEventType
}

public abstract record MetricParseResult
{
    private MetricParseResult() { }
    public sealed record OrdinaryLine : MetricParseResult;
    public sealed record Metric(MetricEvent Event) : MetricParseResult;
    public sealed record Error(MetricProtocolError Code) : MetricParseResult;
}

/// <summary>Stateless parser of one complete stdout line. The caller retains the original log bytes.</summary>
public static class MetricProtocol
{
    public const int MaxEventBytes = 8 * 1024;
    public const int MaxJsonDepth = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly MetricParseResult Ordinary = new MetricParseResult.OrdinaryLine();

    public static MetricParseResult Parse(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> marker = "@researchlab/v1"u8;
        // Require a token boundary: v10 and embedded/indented prefixes remain ordinary logs.
        if (!line.StartsWith(marker) || (line.Length > marker.Length &&
            line[marker.Length] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')))
            return Ordinary;

        if (line.EndsWith("\n"u8))
        {
            line = line[..^1];
            if (line.EndsWith("\r"u8))
                line = line[..^1];
        }
        if (line.Length > MaxEventBytes)
            return Error(MetricProtocolError.EventTooLarge);
        try
        {
            _ = StrictUtf8.GetCharCount(line);
            if (line.Length == marker.Length || line[marker.Length] != (byte)' ')
                return Error(MetricProtocolError.InvalidEnvelope);
            var payload = line[(marker.Length + 1)..];
            if (payload.Contains((byte)'\n') || payload.Contains((byte)'\r'))
                return Error(MetricProtocolError.InvalidEnvelope);
            using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth
            });
            return ParseObject(document.RootElement);
        }
        catch (DecoderFallbackException) { return Error(MetricProtocolError.InvalidUtf8); }
        catch (JsonException) { return Error(MetricProtocolError.InvalidJson); }
        catch (InvalidOperationException) { return Error(MetricProtocolError.InvalidJson); }
        catch (ArgumentException) { return Error(MetricProtocolError.InvalidJson); }
    }

    private static MetricParseResult ParseObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Error(MetricProtocolError.InvalidEnvelope);
        // Scan all keys first, including decoded escape-equivalent duplicates.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!keys.Add(property.Name))
                return Error(MetricProtocolError.DuplicateField);
        if (!root.TryGetProperty("type", out var type))
            return Error(MetricProtocolError.MissingField);
        if (type.ValueKind != JsonValueKind.String)
            return Error(MetricProtocolError.InvalidFieldType);
        if (type.GetString() != "metric")
            return Error(MetricProtocolError.UnsupportedEventType);
        if (keys.Any(key => key is not ("type" or "name" or "value" or "step" or "unit")))
            return Error(MetricProtocolError.UnknownField);
        if (!root.TryGetProperty("name", out var name) || !root.TryGetProperty("value", out var value))
            return Error(MetricProtocolError.MissingField);
        if (name.ValueKind != JsonValueKind.String || value.ValueKind != JsonValueKind.Number)
            return Error(MetricProtocolError.InvalidFieldType);
        var metricName = name.GetString()!;
        if (!ValidText(metricName))
            return Error(MetricProtocolError.InvalidName);
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            return Error(MetricProtocolError.InvalidValue);
        long? step = null;
        if (root.TryGetProperty("step", out var stepElement))
        {
            if (stepElement.ValueKind != JsonValueKind.Number)
                return Error(MetricProtocolError.InvalidFieldType);
            if (!stepElement.TryGetInt64(out var integer))
                return Error(MetricProtocolError.InvalidStep);
            step = integer;
        }
        string? unit = null;
        if (root.TryGetProperty("unit", out var unitElement))
        {
            if (unitElement.ValueKind != JsonValueKind.String)
                return Error(MetricProtocolError.InvalidFieldType);
            unit = unitElement.GetString()!;
            if (!ValidText(unit))
                return Error(MetricProtocolError.InvalidUnit);
        }
        return new MetricParseResult.Metric(new MetricEvent(metricName, number, step, unit));
    }

    private static bool ValidText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return false;
        // Reject unpaired UTF-16 surrogates introduced by JSON escapes as well.
        _ = StrictUtf8.GetByteCount(value);
        return true;
    }

    private static MetricParseResult.Error Error(MetricProtocolError code) => new(code);
}
