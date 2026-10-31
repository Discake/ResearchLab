using System.Diagnostics;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

// All files, processes and measurements in these tests are synthetic fixtures.
internal sealed class TestWorkspace : IDisposable
{
    private readonly List<string> links = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResearchLab.Tests", Guid.NewGuid().ToString("N"));
    public string Workspace => Path.Combine(Root, "project with spaces");
    public string Database => Path.Combine(Root, "research.db");
    public string Configuration => Path.Combine(Workspace, "researchlab.json");
    public string Executable => Path.Combine(Workspace, "task.cmd");
    public SqliteResearchStore Store => new(Database);
    public static DateTimeOffset Epoch => new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public TestWorkspace()
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Executable, "@echo fixture-executed>sentinel.txt\r\n");
        WriteConfiguration();
    }

    public void WriteConfiguration(string executable = "./task.cmd", string[]? args = null, int timeout = 10) =>
        File.WriteAllText(Configuration, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            tasks = new { benchmark = new { executable, args = args ?? [], timeoutSeconds = timeout } }
        }));

    public ProjectCatalog Catalog() => new(Store, new WorkspaceInspector());

    public async Task<(Project Project, Experiment Experiment, Run Run)> SeedRunAsync()
    {
        var store = Store;
        await store.InitializeAsync();
        var project = new Project(ProjectId.New(), "Fixture project", Workspace, Epoch);
        await store.AddProjectAsync(project);
        var experiment = new Experiment(ExperimentId.New(), project.Id, "Fixture experiment", "Synthetic", "benchmark",
            [new ParameterDefinition("n", ParameterType.Int, required: true, defaultValue: ParameterValue.From(2), minimum: 1, maximum: 3,
                allowedValues: [ParameterValue.From(1), ParameterValue.From(2)])], Epoch);
        await store.AddExperimentAsync(experiment);
        var run = Run.Create(RunId.New(), experiment, new TaskSnapshot("benchmark", "./task.cmd", ["--fixture"], TimeSpan.FromSeconds(10)),
            new Dictionary<string, ParameterValue>(), new SourceSnapshot("fixture-sha", true, "fixture-remote"),
            new EnvironmentSnapshot("fixture-os", "fixture-cpu", new Dictionary<string, string> { ["compiler"] = "fixture-version" }), Epoch);
        await store.AddRunAsync(run);
        return (project, experiment, run);
    }

    public async Task<string> GitAsync(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = Workspace, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        if (args is ["init"])
            await GitAsync("config", "core.autocrlf", "false");
        return (await output).Trim();
    }

    public void Dispose()
    {
        var allowedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ResearchLab.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowedParent, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe fixture cleanup path.");
        if (Directory.Exists(Root))
        {
            foreach (var link in links.AsEnumerable().Reverse())
                Directory.Delete(link);
            foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, recursive: true);
        }
    }

    public async Task LinkDirectoryAsync(string link, string target)
    {
        if (!Path.GetFullPath(link).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !Path.GetFullPath(target).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Links must stay within the fixture root.");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            links.Add(link);
            return;
        }
        // Windows junctions do not require the optional symbolic-link privilege.
        // Both paths are generated by this fixture, never taken from repository content.
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        links.Add(link);
        await output;
    }
}
