using System.Text;

internal static class MetricFixture
{
    internal const string First = "@researchlab/v1 {\"type\":\"metric\",\"name\":\"residual\",\"value\":0.5,\"step\":1,\"unit\":\"1\"}\n";
    internal const string Second = "@researchlab/v1 {\"type\":\"metric\",\"name\":\"residual\",\"value\":0.2,\"step\":2,\"unit\":\"1\"}\n";

    public static async Task<int> RunAsync(string mode)
    {
        if (mode == "metric-demo")
        {
            using var input = System.Text.Json.JsonDocument.Parse(Environment.GetEnvironmentVariable("RESEARCHLAB_PARAMETERS_JSON")!);
            var n = input.RootElement.GetProperty("n").GetInt32();
            Console.Error.WriteLine("Demonstration calculations, not a benchmark.");
            foreach (var step in new[] { 1, 1, 2 })
                Console.WriteLine("@researchlab/v1 " + System.Text.Json.JsonSerializer.Serialize(new
                { type = "metric", name = "demo_square", value = n * step * n * step, step, unit = "1" }));
            Console.WriteLine("@researchlab/v1 " + System.Text.Json.JsonSerializer.Serialize(new
            { type = "metric", name = "demo_input", value = n }));
            return 0;
        }
        using var stdout = Console.OpenStandardOutput();
        using var stderr = Console.OpenStandardError();
        if (mode is "metric-early" or "metric-ordinary-early")
        {
            if (mode == "metric-ordinary-early")
            {
                await stdout.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 64 * 1024) + "\n"));
                await stdout.FlushAsync();
            }
            // One write ensures the first read already contains complete metric lines.
            // Signal before any flush which can wait for pipe consumption on Windows.
            await stdout.WriteAsync(Encoding.UTF8.GetBytes(First + Second));
            await File.WriteAllTextAsync("synthetic-metrics-ready", "ready");
            return 0;
        }
        await stdout.WriteAsync("Starting...\r\n"u8.ToArray());
        var errors = mode is "metric-stderr" or "metric-many-stderr" ? WriteLargeAsync(stderr, 512 * 1024) : Task.CompletedTask;
        if (mode is "metric-many" or "metric-many-stderr" or "metric-wait" or "metric-overflow")
        {
            var count = mode is "metric-many" or "metric-many-stderr" ? 2000 : 130;
            for (var index = 0; index < count; index++)
                await stdout.WriteAsync(Encoding.UTF8.GetBytes($"@researchlab/v1 {{\"type\":\"metric\",\"name\":\"residual\",\"value\":{index},\"step\":{index},\"unit\":\"1\"}}\n"));
        }
        else
        {
            if (mode == "metric-split")
            {
                // Split inside the marker, JSON tokens, UTF-8 and CRLF, with real pipe writes.
                var bytes = Encoding.UTF8.GetBytes(First.Replace("\"1\"", "\"µs\"").Replace("\n", "\r\n"));
                foreach (var value in bytes)
                {
                    await stdout.WriteAsync(new[] { value });
                    await stdout.FlushAsync();
                    await Task.Delay(2);
                }
            }
            else
                await stdout.WriteAsync(Encoding.UTF8.GetBytes(First));
            if (mode == "metric-invalid")
                await stdout.WriteAsync("@researchlab/v1 {\"type\":\"metric\",\"name\":\"private-secret\",\"value\":\"bad\"}\n"u8.ToArray());
            if (mode == "metric-unknown")
                await stdout.WriteAsync("@researchlab/v1 {\"type\":\"artifact\",\"path\":\"private-secret\"}\n"u8.ToArray());
            if (mode == "metric-unit")
                await stdout.WriteAsync(Encoding.UTF8.GetBytes(Second.Replace("\"1\"", "\"s\"")));
            if (mode == "metric-diagnostics")
                for (var index = 0; index < 400; index++)
                    await stdout.WriteAsync("@researchlab/v1 {\n"u8.ToArray());
            if (mode == "metric-oversized")
            {
                await stdout.WriteAsync(Encoding.UTF8.GetBytes("@researchlab/v1 {\"type\":\"metric\",\"name\":\"" + new string('x', 20000) + "\",\"value\":3}\n"));
                // A long ordinary line must also remain bounded without generating a metric.
                await stdout.WriteAsync(Encoding.UTF8.GetBytes(new string('y', 20000) + "\n"));
            }
            var second = mode == "metric-split" ? Second.Replace("\"1\"", "\"µs\"") : Second;
            if (mode == "metric-eof") second = second.TrimEnd('\n');
            await stdout.WriteAsync(Encoding.UTF8.GetBytes(second));
        }
        if (mode != "metric-eof")
            await stdout.WriteAsync("Completed.\n"u8.ToArray());
        await stdout.FlushAsync();
        // Test synchronization only; this is not registered evidence or an artifact.
        await File.WriteAllTextAsync("synthetic-metrics-ready", "ready");
        await errors;
        if (mode == "metric-stderr")
            await stderr.WriteAsync(Encoding.UTF8.GetBytes(First)); // never a metric source
        if (mode == "metric-overflow")
            await Task.WhenAll(WriteLargeAsync(stdout, 2 * 1024 * 1024), WriteLargeAsync(stderr, 2 * 1024 * 1024));
        if (mode == "metric-wait") await Task.Delay(TimeSpan.FromSeconds(30));
        return mode == "metric-fail" ? 7 : 0;
    }

    private static async Task WriteLargeAsync(Stream stream, int length)
    {
        var buffer = new byte[4096];
        Array.Fill(buffer, (byte)'x');
        for (var count = 0; count < length; count += buffer.Length)
            await stream.WriteAsync(buffer);
    }
}
