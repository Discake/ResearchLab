using Xunit;

namespace ResearchLab.Domain.Tests;

public sealed class ParameterTests
{
    [Fact]
    public void ValidatesAllFourTypesAndAppliesDefaultsBeforeRequiredCheck()
    {
        var experiment = DomainFixtures.Experiment(
            new("count", ParameterType.Int, required: true, minimum: 1, maximum: 10),
            new("tolerance", ParameterType.Double, required: true, defaultValue: ParameterValue.From(0.1)),
            new("enabled", ParameterType.Bool),
            new("method", ParameterType.String, allowedValues: [ParameterValue.From("rbf")]),
            new("optional", ParameterType.String));
        var values = experiment.Definition.ValidateParameters(new Dictionary<string, ParameterValue>
        {
            ["count"] = ParameterValue.From(10),
            ["enabled"] = ParameterValue.From(false),
            ["method"] = ParameterValue.From("rbf")
        });

        Assert.Equal(4, values.Count);
        Assert.Equal(ParameterValue.From(10), values["count"]);
        Assert.Equal(ParameterValue.From(0.1), values["tolerance"]);
        Assert.Equal(ParameterValue.From(false), values["enabled"]);
        Assert.Equal(ParameterValue.From("rbf"), values["method"]);
        Assert.False(values.ContainsKey("optional"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void IntegerBoundsAreInclusive(int value)
    {
        new ParameterDefinition("n", ParameterType.Int, minimum: 1, maximum: 10).Validate(ParameterValue.From(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void RejectsIntegerOutsideRange(int value)
    {
        Assert.Equal("ParameterOutOfRange", Assert.Throws<DomainException>(() =>
            new ParameterDefinition("n", ParameterType.Int, minimum: 1, maximum: 10)
                .Validate(ParameterValue.From(value))).Code);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    public void DoubleBoundsAreInclusive(double value)
    {
        new ParameterDefinition("n", ParameterType.Double, minimum: 0.1, maximum: 0.5).Validate(ParameterValue.From(value));
    }

    [Fact]
    public void RejectsMissingUnknownWrongTypeAndDisallowedValues()
    {
        var definition = DomainFixtures.Experiment(new ParameterDefinition("n", ParameterType.Int, required: true,
            allowedValues: [ParameterValue.From(1)])).Definition;
        Assert.Equal("MissingParameter", Assert.Throws<DomainException>(() =>
            definition.ValidateParameters(new Dictionary<string, ParameterValue>())).Code);
        Assert.Equal("ParameterTypeMismatch", Assert.Throws<DomainException>(() =>
            definition.ValidateParameters(new Dictionary<string, ParameterValue> { ["n"] = ParameterValue.From(1.0) })).Code);
        Assert.Equal("ParameterNotAllowed", Assert.Throws<DomainException>(() =>
            definition.ValidateParameters(new Dictionary<string, ParameterValue> { ["n"] = ParameterValue.From(2) })).Code);
        Assert.Equal("UnknownParameter", Assert.Throws<DomainException>(() =>
            definition.ValidateParameters(new Dictionary<string, ParameterValue>
            { ["n"] = ParameterValue.From(1), ["typo"] = ParameterValue.From(1) })).Code);
    }

    [Fact]
    public void ParameterNamesAreOrdinalEvenWithCaseInsensitiveInput()
    {
        var definition = DomainFixtures.Experiment(new ParameterDefinition("n", ParameterType.Int)).Definition;
        Assert.Equal("UnknownParameter", Assert.Throws<DomainException>(() => definition.ValidateParameters(
            new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase) { ["N"] = ParameterValue.From(1) })).Code);
    }

    [Fact]
    public void RejectsInvalidDefinitionsBeforeAnyRunIsCreated()
    {
        Assert.Throws<DomainException>(() => new ParameterDefinition(" ", ParameterType.Int));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", (ParameterType)99));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.String, minimum: 0));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Int, minimum: 2, maximum: 1));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Double, minimum: double.NaN));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Double, maximum: double.PositiveInfinity));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Int, allowedValues: []));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Int, allowedValues: [ParameterValue.From("1")]));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Int,
            minimum: 1, defaultValue: ParameterValue.From(0)));
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Int,
            allowedValues: [ParameterValue.From(1)], defaultValue: ParameterValue.From(2)));
        Assert.Throws<DomainException>(() => DomainFixtures.Experiment(new("n", ParameterType.Int), new("n", ParameterType.Bool)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteParameters(double value)
    {
        Assert.Equal("NonFiniteParameter", Assert.Throws<DomainException>(() => ParameterValue.From(value)).Code);
    }

    [Fact]
    public void RejectsUnsupportedMutableAndNullValues()
    {
        foreach (var value in new object?[] { null, 1L, 1m, 1f, new int[] { 1 }, new object() })
            Assert.Equal("UnsupportedParameterType", Assert.Throws<DomainException>(() => ParameterValue.From(value)).Code);
        Assert.Throws<DomainException>(() => new ParameterDefinition("n", ParameterType.Int).Validate(null!));
    }
}
