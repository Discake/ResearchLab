using System.Reflection;
using Xunit;

namespace ResearchLab.Domain.Tests;

public sealed class ArchitectureSmokeTests
{
    [Fact]
    public void DomainAssemblyLoadsWithoutApplicationOrThirdPartyDependencies()
    {
        var domain = Assembly.Load("ResearchLab.Domain");

        Assert.Equal("ResearchLab.Domain", domain.GetName().Name);
        Assert.All(domain.GetReferencedAssemblies(), reference =>
            Assert.True(
                reference.Name == "System.Runtime",
                $"Unexpected Domain dependency: {reference.Name}. Review the domain boundary before allowing it."));
    }
}
