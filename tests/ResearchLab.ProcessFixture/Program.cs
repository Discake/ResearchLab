using System.Diagnostics;
using System.Text.Json;

// Synthetic subprocess fixture for integration tests, not a ResearchLab CLI or sample evidence.
switch (args.FirstOrDefault())
{
    case "crash-root" when OperatingSystem.IsWindows():
        return CrashFixture.Root(args);
    case "crash-child" when OperatingSystem.IsWindows():
        return CrashFixture.Child(args);
    case "crash-cli-parent" when OperatingSystem.IsWindows():
        return await CrashFixture.CliParentAsync(args);
    case "native-echo" or "native-owner" or "native-tree" or "native-hold" or "native-marker" or "native-resources" or "group-root" or "group-child":
        return await NativeLauncherFixture.RunAsync(args);
    case "native-exit-259":
        return 259;
    case "closed-job-owner":
        return await ClosedJobFixture.RunAsync(args);
    case "interrupt-cli" when OperatingSystem.IsWindows():
        return await WindowsConsoleInterrupt.RunAsync(args);
    case "owner-session":
        return await HoldOwnershipAsync(args);
    case "success":
        return 0;
    case "output-directory":
        var outputDirectory = Environment.GetEnvironmentVariable("RESEARCHLAB_OUTPUT_DIR")!;
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "synthetic-result.txt"), Environment.CurrentDirectory);
        return 0;
    case "fail":
        Console.Error.WriteLine("Synthetic failure.");
        return 7;
    case "literal":
        return args.Skip(1).SequenceEqual(new[] { "a b", "$(command)", "; echo forbidden", "%PATH%" }) ? 0 : 8;
    case "parameters":
        using (var json = JsonDocument.Parse(Environment.GetEnvironmentVariable("RESEARCHLAB_PARAMETERS_JSON")!))
            return json.RootElement.GetProperty("n").GetInt32() == 2 ? 0 : 9;
    case "streams":
        await Task.WhenAll(WriteAsync(Console.Out, 256 * 1024), WriteAsync(Console.Error, 256 * 1024));
        return 0;
    case "limit":
        await Task.WhenAll(WriteAsync(Console.Out, 512 * 1024), WriteAsync(Console.Error, 512 * 1024));
        return 0;
    case "overflow":
        await Task.WhenAll(WriteAsync(Console.Out, 2 * 1024 * 1024), WriteAsync(Console.Error, 2 * 1024 * 1024));
        return 0;
    case "logged-wait":
        Console.Out.WriteLine("Synthetic waiting stdout.");
        Console.Error.WriteLine("Synthetic waiting stderr.");
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "wait":
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "tree":
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            ArgumentList = { "wait" }
        })!)
        {
            await File.WriteAllTextAsync("synthetic-child.pid", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        return 0;
    default:
        return 10;
}

static async Task<int> HoldOwnershipAsync(string[] arguments)
{
    var store = new ResearchLab.Infrastructure.Persistence.SqliteResearchStore(arguments[1]);
    var experiment = (await store.GetExperimentAsync(new ResearchLab.Domain.ExperimentId(Guid.Parse(arguments[2]))))!;
    using var session = store.OpenRunSession();
    var now = DateTimeOffset.UtcNow;
    var run = ResearchLab.Domain.Run.Create(ResearchLab.Domain.RunId.New(), experiment,
        new ResearchLab.Domain.TaskSnapshot("benchmark", "synthetic-owner", [], TimeSpan.FromSeconds(30)),
        new Dictionary<string, ResearchLab.Domain.ParameterValue>(),
        new ResearchLab.Domain.SourceSnapshot(null, null, null),
        new ResearchLab.Domain.EnvironmentSnapshot("synthetic", "synthetic", new Dictionary<string, string>()), now);
    await session.AddRunAsync(run);
    if (arguments[3] == "Running")
    {
        run.Start(now, Environment.ProcessId, run.Version);
        await store.SaveRunAsync(run, 0);
    }
    Console.WriteLine(run.Id.Value);
    await Console.Out.FlushAsync();
    // Synthetic owner: the test kills this process to verify OS release after a real crash.
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

static async Task WriteAsync(TextWriter writer, int length)
{
    var chunk = new string('x', 4096);
    for (var count = 0; count < length; count += chunk.Length)
        await writer.WriteAsync(chunk);
}
