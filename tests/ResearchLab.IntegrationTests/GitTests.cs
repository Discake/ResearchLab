using ResearchLab.Infrastructure.Catalog;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class GitTests
{
    [Fact]
    public async Task ReadsCleanDirtyAndUnbornRepositoriesWithoutExecutingRepositoryHooks()
    {
        using var fixture = new TestWorkspace();
        await fixture.GitAsync("init");
        var inspector = new WorkspaceInspector();
        var unborn = await inspector.InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken);
        Assert.Null(unborn.Source.GitHeadSha);
        Assert.True(unborn.Source.IsWorkingTreeDirty);
        await fixture.GitAsync("add", ".");
        await fixture.GitAsync("-c", "user.name=ResearchLab Tests", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "synthetic fixture");
        await fixture.GitAsync("remote", "add", "origin", "https://user:password@example.invalid/repo.git?token=secret");
        var head = await fixture.GitAsync("rev-parse", "HEAD");
        var clean = await inspector.InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken);
        Assert.Equal(head, clean.Source.GitHeadSha);
        Assert.False(clean.Source.IsWorkingTreeDirty);
        Assert.Equal("https://example.invalid/repo.git", clean.Source.RepositoryUrl);

        await fixture.GitAsync("config", "core.fsmonitor", "echo unsafe > sentinel.txt");
        await fixture.GitAsync("config", "filter.unsafe.clean", "echo unsafe > sentinel.txt");
        await fixture.GitAsync("config", "filter.unsafe.process", "echo unsafe > sentinel.txt");
        await fixture.GitAsync("config", "filter.unsafe.required", "true");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, ".gitattributes"), "*.cmd filter=unsafe", TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(fixture.Executable, "dirty fixture", TestContext.Current.CancellationToken);
        var dirty = await inspector.InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken);
        Assert.True(dirty.Source.IsWorkingTreeDirty);
        Assert.Equal(head, dirty.Source.GitHeadSha);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
    }

    [Fact]
    public async Task DoesNotReportCleanWhenSubmoduleStateIsUnknown()
    {
        using var fixture = new TestWorkspace();
        await fixture.GitAsync("init");
        await fixture.GitAsync("add", ".");
        await fixture.GitAsync("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
        var sha = await fixture.GitAsync("rev-parse", "HEAD");
        await fixture.GitAsync("update-index", "--add", "--cacheinfo", $"160000,{sha},nested");
        await fixture.GitAsync("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "fixture gitlink");
        var inspection = await new WorkspaceInspector().InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken);
        Assert.NotNull(inspection.Source.GitHeadSha);
        Assert.Null(inspection.Source.IsWorkingTreeDirty);
        Assert.NotEmpty(inspection.Diagnostics);
    }

    [Fact]
    public async Task MissingGitStillAllowsImportWithUnknownProvenance()
    {
        using var fixture = new TestWorkspace();
        var inspector = new WorkspaceInspector(new GitInspector(Path.Combine(fixture.Root, "missing-git")));
        var inspection = await inspector.InspectAsync(fixture.Workspace, TestContext.Current.CancellationToken);
        Assert.Null(inspection.Source.GitHeadSha);
        Assert.Null(inspection.Source.IsWorkingTreeDirty);
        Assert.Null(inspection.Source.RepositoryUrl);
        Assert.NotEmpty(inspection.Diagnostics);
    }
}
