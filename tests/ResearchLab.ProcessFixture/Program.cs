using System.Diagnostics;
using System.Text.Json;

// Synthetic subprocess fixture for integration tests, not a ResearchLab CLI or sample evidence.
switch (args.FirstOrDefault())
{
    case "success":
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
    case "overflow":
        await Task.WhenAll(WriteAsync(Console.Out, 2 * 1024 * 1024), WriteAsync(Console.Error, 2 * 1024 * 1024));
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

static async Task WriteAsync(TextWriter writer, int length)
{
    var chunk = new string('x', 4096);
    for (var count = 0; count < length; count += chunk.Length)
        await writer.WriteAsync(chunk);
}
