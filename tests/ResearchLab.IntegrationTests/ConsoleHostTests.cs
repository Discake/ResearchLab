using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResearchLab.Cli;
using ResearchLab.Domain;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class ConsoleHostTests
{
    private const string ExampleId = "01000000-0000-0000-0000-000000000001";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("project list", ReadCommand.ProjectList)]
    [InlineData("experiment list --project " + ExampleId, ReadCommand.ExperimentList)]
    [InlineData("run list --experiment " + ExampleId + " --offset 20 --page-size 10", ReadCommand.RunList)]
    [InlineData("run show " + ExampleId, ReadCommand.RunShow)]
    public void ParsesSupportedCommandsAndExplicitPaths(string command, ReadCommand expected)
    {
        var database = Path.Combine(Path.GetTempPath(), "synthetic data", "research.db");
        var directory = Path.Combine(Path.GetTempPath(), "synthetic logs");
        var parsed = CommandArguments.Parse(["--database", database, .. command.Split(' '), "--data-dir", directory]);
        Assert.Equal(expected, parsed.Command);
        Assert.Equal(Path.GetFullPath(database), parsed.DatabasePath);
        Assert.Equal(Path.GetFullPath(directory), parsed.DataDirectory);
        Assert.Equal(expected == ReadCommand.ProjectList ? null : Guid.Parse(ExampleId), parsed.Id);
        Assert.Equal(expected == ReadCommand.RunList ? 20 : 0, parsed.Page.Offset);
        Assert.Equal(expected == ReadCommand.RunList ? 10 : 50, parsed.Page.PageSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("run start")]
    [InlineData("run show")]
    [InlineData("run show invalid")]
    [InlineData("run show 00000000-0000-0000-0000-000000000000")]
    [InlineData("project list extra")]
    [InlineData("project list --project " + ExampleId)]
    [InlineData("experiment list")]
    [InlineData("run list")]
    [InlineData("run list --experiment invalid")]
    [InlineData("run list --experiment " + ExampleId + " --offset -1")]
    [InlineData("run list --experiment " + ExampleId + " --offset 2147483648")]
    [InlineData("run list --experiment " + ExampleId + " --page-size 0")]
    [InlineData("run list --experiment " + ExampleId + " --page-size 101")]
    [InlineData("project list --unknown value")]
    [InlineData("project list --database duplicate")]
    [InlineData("project list --data-dir duplicate")]
    [InlineData("experiment list --project")]
    public async Task BadCommandsReturnUsageWithoutTouchingStorage(string command)
    {
        using var fixture = new TestWorkspace();
        var result = await InvokeAsync(fixture, command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("--help", result.Error);
        Assert.False(File.Exists(fixture.Database));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "logs")));
    }

    [Theory]
    [InlineData("--database")]
    [InlineData("--data-dir")]
    public async Task MissingConfigurationReturnsUsage(string option)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(2, await ConsoleHost.RunAsync(["project", "list", option, "synthetic"], output, error, Token));
        Assert.Empty(output.ToString());
        Assert.Contains("Required option", error.ToString());
    }

    [Fact]
    public async Task HelpNeedsNoConfigurationAndReturnsSuccess()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await ConsoleHost.RunAsync(["--help"], output, error, Token));
        Assert.Contains("run show", output.ToString());
        Assert.DoesNotContain("run start", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ReadsCatalogHistoryDetailsAndKnownLogPathsWithoutMutatingData()
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, run) = await fixture.SeedRunAsync();
        run.Start(TestWorkspace.Epoch, 123, run.Version);
        run.Fail(TestWorkspace.Epoch.AddSeconds(1), "Synthetic secret /private/path", run.Version, 7);
        await fixture.Store.SaveRunAsync(run, 0, Token);
        var databaseBefore = await File.ReadAllBytesAsync(fixture.Database, Token);
        var projects = await InvokeAsync(fixture, "project", "list");
        AssertSuccess(projects);
        using (var json = JsonDocument.Parse(projects.Output))
        {
            Assert.Equal(project.Id.Value, json.RootElement[0].GetProperty("id").GetGuid());
            Assert.Equal(project.Name, json.RootElement[0].GetProperty("name").GetString());
            Assert.False(json.RootElement[0].TryGetProperty("workspacePath", out _));
        }
        var experiments = await InvokeAsync(fixture, "experiment", "list", "--project", project.Id.Value.ToString());
        AssertSuccess(experiments);
        using (var json = JsonDocument.Parse(experiments.Output))
            Assert.Equal(experiment.Id.Value, json.RootElement[0].GetProperty("id").GetGuid());
        var runs = await InvokeAsync(fixture, "run", "list", "--experiment", experiment.Id.Value.ToString(), "--page-size", "1");
        AssertSuccess(runs);
        using (var json = JsonDocument.Parse(runs.Output))
            Assert.Equal(run.Id.Value, json.RootElement[0].GetProperty("id").GetGuid());
        var emptyPage = await InvokeAsync(fixture, "run", "list", "--experiment", experiment.Id.Value.ToString(), "--offset", "1");
        AssertSuccess(emptyPage);
        using (var json = JsonDocument.Parse(emptyPage.Output))
            Assert.Equal(0, json.RootElement.GetArrayLength());
        var show = await InvokeAsync(fixture, "run", "show", run.Id.Value.ToString());
        AssertSuccess(show);
        using (var json = JsonDocument.Parse(show.Output))
        {
            var details = json.RootElement;
            Assert.Equal("Failed", details.GetProperty("status").GetString());
            Assert.Equal(2, details.GetProperty("parameters").GetProperty("n").GetInt32());
            Assert.Equal(7, details.GetProperty("exitCode").GetInt32());
            Assert.Equal(run.CreatedAtUtc, details.GetProperty("createdAtUtc").GetDateTimeOffset());
            Assert.Equal(run.FinishedAtUtc, details.GetProperty("finishedAtUtc").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, details.GetProperty("logs").ValueKind);
        }
        Assert.DoesNotContain("secret", show.Output);
        Assert.DoesNotContain(fixture.Root, show.Output);
        var logDirectory = Path.Combine(fixture.Root, "logs", run.Id.Value.ToString("N"));
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "stdout.log"), "Synthetic stdout", Token);
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "stderr.log"), "Synthetic stderr", Token);
        var withLogs = await InvokeAsync(fixture, "run", "show", run.Id.Value.ToString());
        AssertSuccess(withLogs);
        using (var json = JsonDocument.Parse(withLogs.Output))
            Assert.Equal(Path.Combine(run.Id.Value.ToString("N"), "stdout.log"),
                json.RootElement.GetProperty("logs").GetProperty("stdout").GetString());
        Assert.Equal(databaseBefore, await File.ReadAllBytesAsync(fixture.Database, Token));
        Assert.False(Directory.Exists(fixture.Database + ".sessions"));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "sentinel.txt")));
    }

    [Fact]
    public async Task EmptyListsAreSuccessfulAndMissingRecordsHaveDistinctExitCode()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(Token);
        var empty = await InvokeAsync(fixture, "project", "list");
        AssertSuccess(empty);
        using (var json = JsonDocument.Parse(empty.Output))
            Assert.Equal(0, json.RootElement.GetArrayLength());
        foreach (var command in new[]
        {
            new[] { "experiment", "list", "--project", ExampleId },
            new[] { "run", "list", "--experiment", ExampleId },
            new[] { "run", "show", ExampleId }
        })
        {
            var missing = await InvokeAsync(fixture, command);
            Assert.Equal(3, missing.Code);
            Assert.Empty(missing.Output);
            Assert.Contains("not found", missing.Error);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("empty")]
    public async Task InvalidDatabaseFailsWithoutCreatingOrMigratingIt(string kind)
    {
        using var fixture = new TestWorkspace();
        if (kind == "corrupt")
            await File.WriteAllTextAsync(fixture.Database, "Synthetic invalid SQLite", Token);
        if (kind == "empty")
            await File.WriteAllBytesAsync(fixture.Database, [], Token);
        var before = File.Exists(fixture.Database) ? await File.ReadAllBytesAsync(fixture.Database, Token) : null;
        var result = await InvokeAsync(fixture, "project", "list");
        Assert.Equal(4, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("Cannot read configured storage", result.Error);
        Assert.DoesNotContain(fixture.Root, result.Error);
        if (before is null)
            Assert.False(File.Exists(fixture.Database));
        else
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Database, Token));
    }

    [Fact]
    public async Task CanceledReadReturns130()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(130, await ConsoleHost.RunAsync(Arguments(fixture, "project", "list"), output, error, cancellation.Token));
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("experiment")]
    [InlineData("run-list")]
    [InlineData("run-show")]
    public async Task NativeEntryPointReadsAnExistingDatabase(string command)
    {
        using var fixture = new TestWorkspace();
        var (project, experiment, run) = await fixture.SeedRunAsync();
        var words = command switch
        {
            "project" => new[] { "project", "list" },
            "experiment" => ["experiment", "list", "--project", project.Id.Value.ToString()],
            "run-list" => ["run", "list", "--experiment", experiment.Id.Value.ToString()],
            _ => ["run", "show", run.Id.Value.ToString()]
        };
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(ConsoleHost).Assembly.Location);
        foreach (var arg in Arguments(fixture, words))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try { await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(Token);
            }
        }
        Assert.True(process.ExitCode == 0, await stderr);
        using var json = JsonDocument.Parse(await stdout);
        var record = command == "run-show" ? json.RootElement : json.RootElement[0];
        var expected = command == "project" ? project.Id.Value : command == "experiment" ? experiment.Id.Value : run.Id.Value;
        Assert.Equal(expected, record.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task UnsafeLogRootReturnsStorageErrorWithoutPartialDetails()
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        await fixture.LinkDirectoryAsync(Path.Combine(fixture.Root, "logs"), fixture.Workspace);
        var result = await InvokeAsync(fixture, "run", "show", run.Id.Value.ToString());
        Assert.Equal(4, result.Code);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(fixture.Root, result.Error);
    }

    [Fact]
    public async Task DataDirectoryCannotBeAnExistingFile()
    {
        using var fixture = new TestWorkspace();
        await fixture.Store.InitializeAsync(Token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "logs"), "Synthetic obstruction", Token);
        var result = await InvokeAsync(fixture, "project", "list");
        Assert.Equal(4, result.Code);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(fixture.Root, result.Error);
    }

    [Fact]
    public async Task StoreReadOnlyModeAlsoRejectsWrites()
    {
        using var fixture = new TestWorkspace();
        var (_, _, run) = await fixture.SeedRunAsync();
        var store = new Infrastructure.Persistence.SqliteResearchStore(fixture.Database, readOnly: true);
        run.Fail(TestWorkspace.Epoch, "Synthetic failure", run.Version);
        var error = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => store.SaveRunAsync(run, 0, Token));
        Assert.Equal(8, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        Assert.Equal(RunStatus.Created, (await fixture.Store.GetRunAsync(run.Id, Token))!.Status);
    }

    private static string[] Arguments(TestWorkspace fixture, params string[] command) =>
        ["--database", fixture.Database, "--data-dir", Path.Combine(fixture.Root, "logs"), .. command];

    private static async Task<(int Code, string Output, string Error)> InvokeAsync(TestWorkspace fixture, params string[] command)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await ConsoleHost.RunAsync(Arguments(fixture, command), output, error, Token);
        return (code, output.ToString(), error.ToString());
    }

    private static void AssertSuccess((int Code, string Output, string Error) result)
    {
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
    }
}
