using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResearchLab.Execution;

// Synthetic native-launch probes. Only the owning integration test authorizes these processes.
internal static class NativeLauncherFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        switch (args[0])
        {
            case "native-echo":
                var handle = new nint(long.Parse(args[1], CultureInfo.InvariantCulture));
                var limits = new WindowsNativeMethods.ExtendedLimits();
                var inheritedJob = QueryInformationJobObject(handle, 9, ref limits,
                    (uint)Marshal.SizeOf<WindowsNativeMethods.ExtendedLimits>(), out _);
                Console.Error.Write("synthetic stderr: Привет\n");
                Console.Write(JsonSerializer.Serialize(new
                {
                    Arguments = args.Skip(2).ToArray(),
                    Cwd = Environment.CurrentDirectory,
                    Parameters = Environment.GetEnvironmentVariable("RESEARCHLAB_PARAMETERS_JSON"),
                    Output = Environment.GetEnvironmentVariable("RESEARCHLAB_OUTPUT_DIR"),
                    Inherited = Environment.GetEnvironmentVariable("PATH"),
                    Stdin = await Console.In.ReadToEndAsync(),
                    InheritedJob = inheritedJob,
                    Pid = Environment.ProcessId
                }));
                return 0;
            case "native-marker":
                await File.WriteAllTextAsync(args[1], "executed");
                return 0;
            case "native-resources":
                return await CheckResourcesAsync(args[1]);
            case "group-root":
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    ArgumentList = { "group-child", args[1] }
                })!)
                {
                    await WaitForFileAsync("group-child-ready");
                    await File.WriteAllTextAsync("group-child.pid", child.Id.ToString(CultureInfo.InvariantCulture));
                    await WaitForFileAsync("group-root-exit");
                }
                return 0;
            case "group-child":
                Console.WriteLine("synthetic child stdout");
                Console.Error.WriteLine("synthetic child stderr");
                if (args[1] == "closed")
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                    CloseHandle(GetStdHandle(-11));
                    CloseHandle(GetStdHandle(-12));
                }
                await File.WriteAllTextAsync("group-child-ready", "ready");
                await WaitForFileAsync("group-release");
                if (args[1] == "overflow")
                {
                    await Console.Out.WriteAsync(new string('x', 2 * 1024 * 1024));
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                if (args[1] != "closed")
                {
                    Console.WriteLine("synthetic child finished");
                    Console.Error.WriteLine("synthetic child finished stderr");
                }
                return 0;
            case "native-hold":
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "native-tree":
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    ArgumentList = { "native-hold", args[1] }
                })!)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (true)
                    {
                        try { if (File.Exists(args[1]) && int.TryParse(await File.ReadAllTextAsync(args[1], timeout.Token), out _)) break; }
                        catch (IOException) { }
                        await Task.Delay(10, timeout.Token);
                    }
                    Console.WriteLine(child.Id);
                    await Console.Out.FlushAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            case "native-owner":
                using (var process = new WindowsNativeProcessLauncher().Launch(Environment.ProcessPath!,
                    ["native-tree", args[1]], Environment.CurrentDirectory, "{}", Environment.CurrentDirectory))
                using (var reader = new StreamReader(process.StandardOutput))
                {
                    var errors = new StreamReader(process.StandardError).ReadToEndAsync();
                    string? child;
                    try { child = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch
                    {
                        process.Job.Dispose();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await process.WaitForRootExitAsync(timeout.Token);
                        Console.Error.WriteLine(await errors.WaitAsync(TimeSpan.FromSeconds(5)));
                        throw;
                    }
                    if (string.IsNullOrEmpty(child))
                        throw new IOException("Synthetic root exited before ready: " + await errors.WaitAsync(TimeSpan.FromSeconds(5)));
                    Console.WriteLine($"{process.ProcessId} {child}");
                    await Console.Out.FlushAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            default: throw new InvalidOperationException();
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static async Task<int> CheckResourcesAsync(string invalidExecutable)
    {
        Environment.SetEnvironmentVariable("RESEARCHLAB_PARAMETERS_JSON", "inherited-wrong-value");
        Environment.SetEnvironmentVariable("RESEARCHLAB_OUTPUT_DIR", "inherited-wrong-value");
        async Task LaunchAndDrain()
        {
            using var process = new WindowsNativeProcessLauncher().Launch(Environment.ProcessPath!,
                ["native-echo", "0"], Environment.CurrentDirectory, "{}", Environment.CurrentDirectory);
            var output = new StreamReader(process.StandardOutput).ReadToEndAsync();
            var error = new StreamReader(process.StandardError).ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (await process.WaitForRootExitAsync(timeout.Token) != 0) throw new IOException("Synthetic resource child failed.");
            using var result = JsonDocument.Parse(await output.WaitAsync(TimeSpan.FromSeconds(15)));
            if (result.RootElement.GetProperty("Parameters").GetString() != "{}" ||
                result.RootElement.GetProperty("Output").GetString() != Environment.CurrentDirectory)
                throw new IOException("Environment replacements were not preserved.");
            await error.WaitAsync(TimeSpan.FromSeconds(15));
        }
        void FailLaunch()
        {
            try
            {
                using var unexpected = new WindowsNativeProcessLauncher().Launch(invalidExecutable, [],
                    Environment.CurrentDirectory, "{}", Environment.CurrentDirectory);
                throw new IOException("Invalid PE unexpectedly launched.");
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode is 193 or 216) { }
        }
        // Warm up runtime I/O and thread pool before measuring handles in this isolated owner.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(LaunchAndDrain)));
        FailLaunch();
        using var self = Process.GetCurrentProcess();
        var before = self.HandleCount;
        for (var i = 0; i < 32; i++) FailLaunch();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(LaunchAndDrain)));
        var delta = self.HandleCount - before;
        Console.WriteLine(delta.ToString(CultureInfo.InvariantCulture));
        return delta <= 4 ? 0 : 11;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int informationClass,
        ref WindowsNativeMethods.ExtendedLimits information, uint size, out uint returned);
}
