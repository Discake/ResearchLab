using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Persistence;

// Only a synthetic test driver. Production CLI creates no console or extra host.
[SupportedOSPlatform("windows")]
internal static class WindowsConsoleInterrupt
{
    private delegate bool ControlHandler(uint signal);
    private static readonly ControlHandler IgnoreSignal = _ => true;

    public static async Task<int> RunAsync(string[] args)
    {
        var output = Console.Out; // Preserve the test pipe across console allocation.
        _ = Console.Error;
        FreeConsole(); // Detach only this synthetic helper from any inherited test console.
        if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
        ShowWindow(GetConsoleWindow(), 0);
        SetConsoleCtrlHandler(IgnoreSignal, true);
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { args[1], "--database", args[2], "--data-dir", args[3], "run", "start", args[4],
                    "--parameters", "{\"delayMilliseconds\":30000}" }
            };
            using var cli = Process.Start(start)!;
            var stdout = cli.StandardOutput.ReadToEndAsync();
            var stderr = cli.StandardError.ReadToEndAsync();
            await cli.StandardInput.WriteLineAsync("yes");
            cli.StandardInput.Close();
            try
            {
                var store = new SqliteResearchStore(args[2], readOnly: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Run? running = null;
                while (running is null)
                {
                    if (cli.HasExited) throw new InvalidOperationException("CLI exited before the signal: " + await stderr);
                    running = (await store.ListRunsAsync(new ExperimentId(Guid.Parse(args[4])), new RunPageRequest(), timeout.Token))
                        .SingleOrDefault(run => run.Status == RunStatus.Running);
                    if (running is null) await Task.Delay(10, timeout.Token);
                }
                using var child = Process.GetProcessById(running.ProcessId!.Value);
                if (!GenerateConsoleCtrlEvent(0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                await cli.WaitForExitAsync(timeout.Token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    cli.ExitCode,
                    Stdout = await stdout,
                    Stderr = await stderr,
                    ChildExited = child.HasExited
                }));
                return 0;
            }
            finally
            {
                if (!cli.HasExited) { cli.Kill(entireProcessTree: true); await cli.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally
        {
            SetConsoleCtrlHandler(IgnoreSignal, false);
            FreeConsole();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ControlHandler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
}
