using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using ResearchLab.Cli;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

// The external supervisor owns process handles and readiness events, NEVER the Run's Job.
[SupportedOSPlatform("windows")]
internal sealed class WindowsCrashCase : IAsyncDisposable
{
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    internal static string FixtureExe => Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ResearchLab.ProcessFixture.exe");
    internal static string Dotnet => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    internal TestWorkspace Workspace { get; } = new();
    internal string Prefix { get; } = "Local\\ResearchLab.Crash." + Guid.NewGuid().ToString("N");
    internal string Token { get; } = "synthetic-" + Guid.NewGuid().ToString("N");
    internal string Logs => Path.Combine(Workspace.Root, "logs");
    private string? database;
    internal string Database => database ?? Workspace.Database;
    internal SqliteResearchStore Store => new(Database);
    internal string FinallyFile => Path.Combine(Workspace.Root, "cli-finally.txt");
    internal Experiment Experiment { get; private set; } = null!;
    internal Run Run { get; private set; } = null!;
    internal string OutputDirectory => Path.Combine(Logs, Run.Id.Value.ToString("N"), "output");
    internal Process Cli { get; private set; } = null!;
    internal Process? Root { get; private set; }
    internal List<Process> Children { get; } = [];
    internal Task<string>? Stdout { get; private set; }
    internal Task<string>? Stderr { get; private set; }
    private readonly Dictionary<string, EventWaitHandle> events = [];
    private NamedPipeServerStream? checkpoint;
    private StreamReader? checkpointReader;
    private StreamWriter? checkpointWriter;
    private string? stage;
    private bool checkpointReleased;

    private WindowsCrashCase()
    {
        foreach (var name in new[] { "RootReady", "RootExit", "Release", "ChildReady.0", "ChildReady.1", "ParentObservedExit", "ParentRelease" })
            events.Add(name, new EventWaitHandle(false, EventResetMode.ManualReset, Prefix + "." + name));
    }

    internal static async Task<WindowsCrashCase> CreateAsync(int children = 2, bool isolated = false, string? checkpointStage = null,
        WindowsCrashCase? sharedStore = null)
    {
        var test = new WindowsCrashCase { stage = checkpointStage, database = sharedStore?.Database };
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(FixtureExe)!))
                File.Copy(file, Path.Combine(test.Workspace.Workspace, Path.GetFileName(file)), overwrite: true);
            test.Workspace.WriteConfiguration("ResearchLab.ProcessFixture.exe",
                ["crash-root", test.Prefix, test.Token, isolated ? "isolated" : "held", children.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    checkpointStage is null ? "bulk" : "small"], 60);
            await test.Store.InitializeAsync();
            var project = new Project(ProjectId.New(), "Synthetic crash test", test.Workspace.Workspace, DateTimeOffset.UnixEpoch);
            await test.Store.AddProjectAsync(project);
            test.Experiment = new Experiment(ExperimentId.New(), project.Id, "Synthetic crash experiment", "", "benchmark",
                [new ParameterDefinition("label", ParameterType.String, defaultValue: ParameterValue.From(test.Token))], DateTimeOffset.UnixEpoch);
            await test.Store.AddExperimentAsync(test.Experiment);
            if (checkpointStage is not null)
                test.checkpoint = new NamedPipeServerStream("ResearchLab-checkpoint-" + test.Token, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return test;
        }
        catch { await test.DisposeAsync(); throw; }
    }

    internal string[] CliArguments => ["--database", Database, "--data-dir", Logs, "run", "start", Experiment.Id.Value.ToString()];
    internal string CliDll => stage is null ? typeof(ConsoleHost).Assembly.Location : Path.Combine(AppContext.BaseDirectory, "CrashTestCli", "researchlab.dll");

    internal ProcessStartInfo StartInfo()
    {
        var start = new ProcessStartInfo(Dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(CliDll);
        foreach (var argument in CliArguments) start.ArgumentList.Add(argument);
        if (stage is not null)
        {
            start.Environment["RESEARCHLAB_CRASH_CHECKPOINT"] = stage;
            start.Environment["RESEARCHLAB_CRASH_PIPE"] = "ResearchLab-checkpoint-" + Token;
            start.Environment["RESEARCHLAB_CRASH_FINALLY_FILE"] = FinallyFile;
        }
        return start;
    }

    internal async Task StartAsync(Action<ProcessStartInfo>? configure = null)
    {
        var start = StartInfo();
        configure?.Invoke(start);
        Cli = Process.Start(start)!;
        _ = Cli.Handle; // Acquire the observed identity while it is alive; no PID-only exit check.
        Stdout = Cli.StandardOutput.ReadToEndAsync();
        Stderr = Cli.StandardError.ReadToEndAsync();
        await Cli.StandardInput.WriteLineAsync("yes");
        Cli.StandardInput.Close();
    }

    internal void AttachCli(int pid) { Cli = Process.GetProcessById(pid); _ = Cli.Handle; }

    internal async Task<JsonDocument> AtCheckpointAsync()
    {
        using var timeout = new CancellationTokenSource(Limit);
        await checkpoint!.WaitForConnectionAsync(timeout.Token);
        checkpointReader = new StreamReader(checkpoint, leaveOpen: true);
        checkpointWriter = new StreamWriter(checkpoint, leaveOpen: true) { AutoFlush = true };
        var message = JsonDocument.Parse((await checkpointReader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(stage, message.RootElement.GetProperty("Stage").GetString());
        Assert.Equal(Cli.Id, message.RootElement.GetProperty("OwnerPid").GetInt32());
        var child = message.RootElement.GetProperty("ChildPid");
        if (child.ValueKind == JsonValueKind.Number) ObserveRoot(child.GetInt32());
        await LoadRunAsync();
        return message;
    }

    internal async Task ContinueAsync()
    {
        await checkpointWriter!.WriteLineAsync("continue");
        checkpointReleased = true;
    }

    internal async Task LoadRunAsync()
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (true)
        {
            var runs = await Store.ListRunsAsync(Experiment.Id, new ResearchLab.Application.RunPageRequest(), timeout.Token);
            if (runs.Count != 0) { Run = Assert.Single(runs); return; }
            if (Cli.HasExited) throw new IOException("Synthetic CLI exited before creating a Run: " + (Stderr is null ? "" : await Stderr));
            await Task.Delay(10, timeout.Token);
        }
    }

    internal async Task SavedStatusAsync(RunStatus status)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (true)
        {
            await LoadRunAsync();
            if (Run.Status == status) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    internal async Task LogsRecordedAsync()
    {
        using var timeout = new CancellationTokenSource(Limit);
        foreach (var name in new[] { "stdout.log", "stderr.log" })
        {
            while (true)
            {
                using var stream = new RunLogFiles(Logs).OpenRead(Run.Id,
                    name == "stdout.log" ? ResearchLab.Application.RunLogStream.Stdout : ResearchLab.Application.RunLogStream.Stderr);
                using var reader = new StreamReader(stream);
                if ((await reader.ReadToEndAsync(timeout.Token)).Contains("root after children " + Token, StringComparison.Ordinal)) break;
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    internal async Task<string> ReadLogAsync(bool stderr)
    {
        using var stream = new RunLogFiles(Logs).OpenRead(Run.Id,
            stderr ? ResearchLab.Application.RunLogStream.Stderr : ResearchLab.Application.RunLogStream.Stdout);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    internal async Task ReadyAsync()
    {
        await EventAsync("RootReady");
        await LoadRunAsync();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(OutputDirectory, "crash-ready.json")));
        var root = manifest.RootElement;
        Assert.Equal(Token, root.GetProperty("Token").GetString());
        Assert.Equal(OutputDirectory, root.GetProperty("OutputDirectory").GetString());
        Assert.Equal(Workspace.Workspace, root.GetProperty("Cwd").GetString());
        using var parameters = JsonDocument.Parse(root.GetProperty("Parameters").GetString()!);
        Assert.Equal(Token, parameters.RootElement.GetProperty("label").GetString());
        ObserveRoot(root.GetProperty("RootPid").GetInt32());
        foreach (var item in root.GetProperty("Children").EnumerateArray())
        {
            var child = Process.GetProcessById(item.GetInt32());
            _ = child.Handle;
            Assert.False(child.HasExited);
            Children.Add(child);
        }
    }

    private void ObserveRoot(int pid)
    {
        if (Root is not null) { Assert.Equal(Root.Id, pid); return; }
        Root = Process.GetProcessById(pid);
        _ = Root.Handle;
    }

    internal Task EventAsync(string name) => Task.Run(() => Assert.True(events[name].WaitOne(Limit), "Readiness event not signaled: " + name));
    internal void Signal(string name) => events[name].Set();

    internal async Task KillCliAsync()
    {
        Assert.False(Cli.HasExited);
        KillOnly(Cli);
        await Cli.WaitForExitAsync().WaitAsync(Limit);
        Assert.Equal(unchecked((int)0xE0000037), Cli.ExitCode);
    }

    internal async Task AssertGroupExitedAsync(bool rootExitedEarlier = false)
    {
        var processes = Children.Prepend(Root!).ToArray();
        await Task.WhenAll(processes.Select(process => process.WaitForExitAsync())).WaitAsync(Limit);
        Assert.All(processes, process => Assert.True(process.HasExited));
        Assert.Equal(rootExitedEarlier, File.Exists(Path.Combine(OutputDirectory, "root-finally.txt")));
        for (var i = 0; i < Children.Count; i++) Assert.False(File.Exists(Path.Combine(OutputDirectory, "child-finally-" + i + ".txt")));
        Assert.False(File.Exists(FinallyFile));
    }

    internal async Task<string[]> SnapshotsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ExperimentSnapshot, ParametersSnapshot, SourceSnapshot, EnvironmentSnapshot, CreatedAtUtc FROM Runs WHERE Id=$id";
        command.Parameters.AddWithValue("$id", Run.Id.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, 5).Select(reader.GetString).ToArray();
    }

    internal async Task<Run> RecoverAsync(bool expectRecovered = true)
    {
        var result = await RunCliAsync(Database, Logs, "run", "recover");
        Assert.Equal(0, result.Code);
        Assert.Contains("Recovery never kills saved PIDs", result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var ids = json.RootElement.GetProperty("recovered").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        Assert.Equal(expectRecovered, ids.Contains(Run.Id.Value));
        return (await Store.GetRunAsync(Run.Id))!;
    }

    internal static async Task<(int Code, string Output, string Error)> RunCliAsync(string database, string logs, params string[] arguments)
    {
        var start = new ProcessStartInfo(Dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(ConsoleHost).Assembly.Location);
        foreach (var argument in new[] { "--database", database, "--data-dir", logs }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(Limit); }
        finally { if (!process.HasExited) { KillOnly(process); await process.WaitForExitAsync().WaitAsync(Limit); } }
        return (process.ExitCode, await output.WaitAsync(Limit), await error.WaitAsync(Limit));
    }

    internal async Task SqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    internal static void KillOnly(Process process)
    {
        if (!TerminateProcess(process.SafeHandle, 0xE0000037)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Signal("Release"); Signal("RootExit"); Signal("ParentRelease");
            if (checkpointWriter is not null && !checkpointReleased && Cli is not null && !Cli.HasExited)
                try { await ContinueAsync(); } catch (IOException) { }
            if (Cli is not null && !Cli.HasExited)
            {
                try { await Cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { KillOnly(Cli); await Cli.WaitForExitAsync().WaitAsync(Limit); }
            }
            foreach (var process in Children.Prepend(Root).Where(process => process is not null))
            {
                if (!process!.HasExited) { KillOnly(process); await process.WaitForExitAsync().WaitAsync(Limit); }
                process.Dispose();
            }
            Cli?.Dispose();
            if (Stdout is not null) await Stdout.WaitAsync(Limit);
            if (Stderr is not null) await Stderr.WaitAsync(Limit);
        }
        finally
        {
            checkpointReader?.Dispose();
            try { checkpointWriter?.Dispose(); } catch (IOException) { } // A deliberately killed peer cannot receive a flush.
            checkpoint?.Dispose();
            foreach (var handle in events.Values) handle.Dispose();
            Workspace.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
