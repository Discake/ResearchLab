using ResearchLab.Application;
using ResearchLab.Infrastructure.Catalog;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class ImportTests
{
    [Fact]
    public async Task ResolvesWorkspaceAliasAndRejectsExecutableThroughEscapingLinkChain()
    {
        using var fixture = new TestWorkspace();
        var alias = Path.Combine(fixture.Root, "alias");
        await fixture.LinkDirectoryAsync(alias, fixture.Workspace);
        var inspector = new WorkspaceInspector();
        Assert.Equal(fixture.Workspace, (await inspector.InspectAsync(alias, TestContext.Current.CancellationToken)).CanonicalPath);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "outside.cmd"), "fixture", TestContext.Current.CancellationToken);
        var escape = Path.Combine(fixture.Workspace, "escape");
        await fixture.LinkDirectoryAsync(escape, outside);
        var indirect = Path.Combine(fixture.Workspace, "indirect");
        await fixture.LinkDirectoryAsync(indirect, escape);
        fixture.WriteConfiguration("indirect/outside.cmd");
        Assert.Equal("UnsafePath", (await Assert.ThrowsAsync<CatalogException>(() =>
            inspector.InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken))).Code);
        Assert.True(File.Exists(Path.Combine(outside, "outside.cmd")));
    }

    [Fact]
    public async Task ImportNeverExecutesAndTrustRequiresExplicitReviewOfCurrentContent()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        var catalog = fixture.Catalog();
        var imported = await catalog.ImportProjectAsync(fixture.Workspace, "Fixture", TestContext.Current.CancellationToken);
        Assert.Null(imported.Source.GitHeadSha);
        Assert.Null(imported.Source.IsWorkingTreeDirty);
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() =>
            catalog.GetTrustedTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken))).Code);
        var review = await catalog.ReviewTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(imported.Project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken);
        var trusted = await catalog.GetTrustedTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken);
        Assert.Equal(review.Fingerprint, trusted.Fingerprint);
        Assert.Equal(review.ExecutablePath, trusted.ExecutablePath);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() =>
            fixture.Catalog().GetTrustedTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken))).Code);

        await File.AppendAllTextAsync(fixture.Executable, "changed binary fixture", TestContext.Current.CancellationToken);
        Assert.Equal("TaskChanged", (await Assert.ThrowsAsync<CatalogException>(() =>
            catalog.ConfirmTaskAsync(imported.Project.Id, "benchmark", review.Fingerprint, TestContext.Current.CancellationToken))).Code);
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() =>
            catalog.GetTrustedTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken))).Code);
        var next = await catalog.ReviewTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken);
        await catalog.ConfirmTaskAsync(imported.Project.Id, "benchmark", next.Fingerprint, TestContext.Current.CancellationToken);
        fixture.WriteConfiguration(args: ["a b", "; echo forbidden"]);
        Assert.Equal("TaskNotTrusted", (await Assert.ThrowsAsync<CatalogException>(() =>
            catalog.GetTrustedTaskAsync(imported.Project.Id, "benchmark", TestContext.Current.CancellationToken))).Code);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
    }

    [Fact]
    public async Task ArgumentsRemainLiteralAndImportDoesNotGrantTrust()
    {
        using var fixture = new TestWorkspace();
        fixture.WriteConfiguration(args: ["a b", "$(command)", "x; y", "%PATH%"]);
        var inspection = await new WorkspaceInspector().InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "a b", "$(command)", "x; y", "%PATH%" }, inspection.Tasks["benchmark"].Arguments);
        Assert.Equal(Path.GetFullPath(fixture.Workspace), inspection.CanonicalPath);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":2,\"tasks\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"tasks\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"tasks\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"tasks\":{\"x\":{\"executable\":\"task.cmd\",\"args\":\"command\",\"timeoutSeconds\":1}}}")]
    [InlineData("{\"schemaVersion\":1,\"tasks\":{\"x\":{\"executable\":\"task.cmd\",\"args\":[],\"timeoutSeconds\":0}}}")]
    [InlineData("{\"schemaVersion\":1,\"tasks\":{\"x\":{\"executable\":\"task.cmd\",\"args\":[],\"timeoutSeconds\":1,\"shell\":true}}}")]
    public async Task RejectsInvalidConfigurationWithoutPersisting(string json)
    {
        using var fixture = new TestWorkspace();
        await File.WriteAllTextAsync(fixture.Configuration, json, TestContext.Current.CancellationToken);
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("InvalidConfiguration", (await Assert.ThrowsAsync<CatalogException>(() => fixture.Catalog().ImportProjectAsync(fixture.Workspace, "Bad", TestContext.Current.CancellationToken))).Code);
        Assert.Empty(await fixture.Store.ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../outside.cmd", "UnsafePath")]
    [InlineData("missing.cmd", "PathNotFound")]
    [InlineData("C:\\outside.cmd", "UnsafePath")]
    [InlineData("/outside.cmd", "UnsafePath")]
    public async Task RejectsEscapingAbsoluteOrMissingExecutable(string path, string code)
    {
        using var fixture = new TestWorkspace();
        fixture.WriteConfiguration(executable: path);
        Assert.Equal(code, (await Assert.ThrowsAsync<CatalogException>(() => new WorkspaceInspector().InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task RejectsMissingWorkspaceAndOversizedConfiguration()
    {
        using var fixture = new TestWorkspace();
        var inspector = new WorkspaceInspector();
        await Assert.ThrowsAsync<CatalogException>(() => inspector.InspectAsync(Path.Combine(fixture.Root, "missing"), TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(fixture.Configuration, new string(' ', 1024 * 1024 + 1), TestContext.Current.CancellationToken);
        Assert.Equal("ConfigurationTooLarge", (await Assert.ThrowsAsync<CatalogException>(() => inspector.InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task CancellationDoesNotCreateProject()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog().ImportProjectAsync(fixture.Workspace, "Canceled", cancellation.Token));
        Assert.Empty(await fixture.Store.ListProjectsAsync(TestContext.Current.CancellationToken));
    }
}
