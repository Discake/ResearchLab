using System.Text;
using ResearchLab.Application;
using Xunit;

namespace ResearchLab.IntegrationTests;

// Pure unit tests hosted in the existing test project; no processes or storage involved.
public sealed class MetricProtocolTests
{
    private const string Prefix = "@researchlab/v1 ";
    private const string Minimal = "{\"type\":\"metric\",\"name\":\"residual\",\"value\":0.0024}";
    private static MetricParseResult Parse(string payload) => MetricProtocol.Parse(Encoding.UTF8.GetBytes(Prefix + payload));

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AcceptsCompleteUtf8LineWithOptionalTerminator(string ending)
    {
        var bytes = Encoding.UTF8.GetBytes(Prefix +
            "{\"unit\":\"неизвестная/µs²\",\"step\":15,\"value\":0.0024,\"name\":\"невязка😀\",\"type\":\"metric\"}" + ending);
        var metric = Assert.IsType<MetricParseResult.Metric>(MetricProtocol.Parse(bytes)).Event;
        Assert.Equal(new MetricEvent("невязка😀", 0.0024, 15, "неизвестная/µs²"), metric);
    }

    [Fact]
    public void OptionalFieldsAreAbsentAndNoScientificUnitWhitelistIsApplied()
    {
        var metric = Assert.IsType<MetricParseResult.Metric>(Parse(Minimal)).Event;
        Assert.Null(metric.Step);
        Assert.Null(metric.Unit);
        Assert.IsType<MetricParseResult.Metric>(Parse(Minimal[..^1] + ",\"unit\":\"kg m / s^2\"}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ordinary log\r\n")]
    [InlineData("please execute something")]
    [InlineData(" @researchlab/v1 {}")]
    [InlineData("log: @researchlab/v1 {}")]
    [InlineData("@researchlab/v10 {}")]
    [InlineData("@researchlab/v1suffix {}")]
    public void OrdinaryLogsRemainAvailableUnchanged(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var original = bytes.ToArray();
        Assert.IsType<MetricParseResult.OrdinaryLine>(MetricProtocol.Parse(bytes));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("{", MetricProtocolError.InvalidJson)]
    [InlineData("{}", MetricProtocolError.MissingField)]
    [InlineData("[]", MetricProtocolError.InvalidEnvelope)]
    [InlineData("null", MetricProtocolError.InvalidEnvelope)]
    [InlineData("{\"type\":1}", MetricProtocolError.InvalidFieldType)]
    [InlineData("{\"type\":\"artifact\",\"path\":\"x\"}", MetricProtocolError.UnsupportedEventType)]
    [InlineData("{\"type\":\"unknown\"}", MetricProtocolError.UnsupportedEventType)]
    [InlineData("{\"type\":\"metric\"}", MetricProtocolError.MissingField)]
    [InlineData("{\"type\":\"metric\",\"name\":\"x\"}", MetricProtocolError.MissingField)]
    [InlineData("{\"type\":\"metric\",\"value\":1}", MetricProtocolError.MissingField)]
    [InlineData("{\"type\":\"metric\",\"name\":null,\"value\":1}", MetricProtocolError.InvalidFieldType)]
    [InlineData("{\"type\":\"metric\",\"name\":\"\",\"value\":1}", MetricProtocolError.InvalidName)]
    [InlineData("{\"type\":\"metric\",\"name\":\"  \",\"value\":1}", MetricProtocolError.InvalidName)]
    [InlineData("{\"type\":\"metric\",\"name\":\"x\\n\",\"value\":1}", MetricProtocolError.InvalidName)]
    [InlineData("{\"type\":\"metric\",\"name\":\"x\",\"value\":1e400}", MetricProtocolError.InvalidValue)]
    [InlineData("{\"type\":\"metric\",\"name\":\"x\",\"value\":-1e400}", MetricProtocolError.InvalidValue)]
    [InlineData("{\"type\":\"metric\",\"name\":\"x\",\"value\":NaN}", MetricProtocolError.InvalidJson)]
    [InlineData("{\"type\":\"metric\",\"type\":\"metric\"}", MetricProtocolError.DuplicateField)]
    [InlineData("{\"type\":\"metric\",\"t\\u0079pe\":\"metric\"}", MetricProtocolError.DuplicateField)]
    [InlineData("{\"type\":\"metric\",\"extra\":1}", MetricProtocolError.UnknownField)]
    public void ReportsBoundedErrorWithoutMetric(string json, MetricProtocolError code)
        => Assert.Equal(new MetricParseResult.Error(code), Parse(json));

    [Theory]
    [InlineData("value", "null", MetricProtocolError.InvalidFieldType)]
    [InlineData("value", "\"1\"", MetricProtocolError.InvalidFieldType)]
    [InlineData("value", "true", MetricProtocolError.InvalidFieldType)]
    [InlineData("value", "[]", MetricProtocolError.InvalidFieldType)]
    [InlineData("step", "null", MetricProtocolError.InvalidFieldType)]
    [InlineData("step", "\"1\"", MetricProtocolError.InvalidFieldType)]
    [InlineData("step", "1.0", MetricProtocolError.InvalidStep)]
    [InlineData("step", "1e0", MetricProtocolError.InvalidStep)]
    [InlineData("step", "9223372036854775808", MetricProtocolError.InvalidStep)]
    [InlineData("unit", "null", MetricProtocolError.InvalidFieldType)]
    [InlineData("unit", "1", MetricProtocolError.InvalidFieldType)]
    [InlineData("unit", "\"\"", MetricProtocolError.InvalidUnit)]
    [InlineData("unit", "\"  \"", MetricProtocolError.InvalidUnit)]
    [InlineData("unit", "\"m\\u0000\"", MetricProtocolError.InvalidUnit)]
    public void ValidatesEachField(string field, string value, MetricProtocolError code)
    {
        var json = field == "value"
            ? "{\"type\":\"metric\",\"name\":\"x\",\"value\":" + value + "}"
            : Minimal[..^1] + ",\"" + field + "\":" + value + "}";
        Assert.Equal(new MetricParseResult.Error(code), Parse(json));
    }

    [Theory]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-1", -1)]
    public void AcceptsSignedInt64Steps(string text, long expected)
        => Assert.Equal(expected, Assert.IsType<MetricParseResult.Metric>(Parse(Minimal[..^1] + ",\"step\":" + text + "}")).Event.Step);

    [Theory]
    [InlineData("1.7976931348623157e308")]
    [InlineData("-1.7976931348623157e308")]
    [InlineData("5e-324")]
    [InlineData("0")]
    public void AcceptsFiniteDoubleBoundaries(string number)
        => Assert.IsType<MetricParseResult.Metric>(Parse("{\"type\":\"metric\",\"name\":\"x\",\"value\":" + number + "}"));

    [Fact]
    public void EnforcesByteLimitBeforeDecodingAndAllowsExactLimitWithCrLf()
    {
        var exact = Prefix + Minimal + new string(' ', MetricProtocol.MaxEventBytes - Prefix.Length - Minimal.Length);
        Assert.IsType<MetricParseResult.Metric>(MetricProtocol.Parse(Encoding.UTF8.GetBytes(exact + "\r\n")));
        Assert.Equal(new MetricParseResult.Error(MetricProtocolError.EventTooLarge),
            MetricProtocol.Parse(Encoding.UTF8.GetBytes(exact + " ")));
        Assert.IsType<MetricParseResult.OrdinaryLine>(MetricProtocol.Parse(new byte[1024 * 1024]));
        var unicode = Prefix + Minimal[..^1] + ",\"unit\":\"" + new string('µ', 4096) + "\"}";
        Assert.Equal(new MetricParseResult.Error(MetricProtocolError.EventTooLarge),
            MetricProtocol.Parse(Encoding.UTF8.GetBytes(unicode)));
    }

    [Fact]
    public void RejectsInvalidUtf8AndEscapedSurrogates()
    {
        var bytes = Encoding.UTF8.GetBytes(Prefix + Minimal).Concat(new byte[] { 0xc0, 0xaf }).ToArray();
        Assert.Equal(new MetricParseResult.Error(MetricProtocolError.InvalidUtf8), MetricProtocol.Parse(bytes));
        Assert.IsType<MetricParseResult.OrdinaryLine>(MetricProtocol.Parse(new byte[] { 0xff }));
        Assert.IsType<MetricParseResult.Error>(Parse(Minimal.Replace("residual", "\\uD800")));
    }

    [Theory]
    [InlineData("@researchlab/v1", MetricProtocolError.InvalidEnvelope)]
    [InlineData("@researchlab/v1\t{}", MetricProtocolError.InvalidEnvelope)]
    [InlineData("@researchlab/v1 {}\n{}", MetricProtocolError.InvalidEnvelope)]
    [InlineData("@researchlab/v1 {} {}", MetricProtocolError.InvalidJson)]
    [InlineData("@researchlab/v1 {\"type\":\"metric\",}", MetricProtocolError.InvalidJson)]
    [InlineData("@researchlab/v1 {/* comment */}", MetricProtocolError.InvalidJson)]
    [InlineData("@researchlab/v1 [[[[[]]]]]", MetricProtocolError.InvalidJson)]
    public void RejectsFramingAndJsonExtensions(string line, MetricProtocolError code)
        => Assert.Equal(new MetricParseResult.Error(code), MetricProtocol.Parse(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void ArbitraryBytesAndMutatedEventsNeverEscapeAsExceptions()
    {
        var random = new Random(4101);
        var valid = Encoding.UTF8.GetBytes(Prefix + Minimal);
        for (var i = 0; i < 5000; i++)
        {
            var bytes = new byte[random.Next(0, 9000)];
            random.NextBytes(bytes);
            if (i % 2 == 0 && bytes.Length >= Prefix.Length)
                Encoding.UTF8.GetBytes(Prefix).CopyTo(bytes, 0);
            Assert.NotNull(MetricProtocol.Parse(bytes));
            var mutated = valid.ToArray();
            mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            Assert.NotNull(MetricProtocol.Parse(mutated));
        }
    }
}
