using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

// Synthetic crash-only executables. Named events are readiness/control evidence, not delays.
[SupportedOSPlatform("windows")]
internal static class CrashFixture
{
    internal static int Root(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var prefix = args[1];
        var token = args[2];
        var isolated = args[3] == "isolated";
        var count = int.Parse(args[4], CultureInfo.InvariantCulture);
        var output = Environment.GetEnvironmentVariable("RESEARCHLAB_OUTPUT_DIR")!;
        var children = new List<Process>();
        var drains = new List<Task<string>>();
        try
        {
            Console.WriteLine("root stdout " + token);
            Console.Error.WriteLine("root stderr " + token);
            if (args.Length > 5 && args[5] == "bulk")
            {
                // Force real log writes past FileStream's buffer before crash; still below the cap.
                var text = string.Concat(Enumerable.Repeat("synthetic log " + token + "\n", 512));
                Console.Out.Write(text);
                Console.Error.Write(text);
            }
            for (var i = 0; i < count; i++)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = isolated,
                    RedirectStandardError = isolated,
                    ArgumentList = { "crash-child", prefix, token, i.ToString(CultureInfo.InvariantCulture) }
                };
                var child = Process.Start(start)!;
                children.Add(child);
                if (isolated)
                {
                    drains.Add(child.StandardOutput.ReadToEndAsync());
                    drains.Add(child.StandardError.ReadToEndAsync());
                }
                Wait(prefix + ".ChildReady." + i);
            }
            if (args.Length > 5 && args[5] == "bulk")
            {
                var text = "root after children " + token + "\n" + string.Concat(Enumerable.Repeat("synthetic log " + token + "\n", 512));
                Console.Out.Write(text);
                Console.Error.Write(text);
            }
            File.WriteAllText(Path.Combine(output, "crash-ready.tmp"), JsonSerializer.Serialize(new
            {
                RootPid = Environment.ProcessId,
                Children = children.Select(p => p.Id).ToArray(),
                Token = token,
                OutputDirectory = output,
                Cwd = Environment.CurrentDirectory,
                Parameters = Environment.GetEnvironmentVariable("RESEARCHLAB_PARAMETERS_JSON")
            }));
            File.Move(Path.Combine(output, "crash-ready.tmp"), Path.Combine(output, "crash-ready.json"));
            Signal(prefix + ".RootReady");
            Wait(prefix + ".RootExit");
            return 0;
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "root-finally.txt"), "managed root finally " + token);
            foreach (var child in children) child.Dispose();
            GC.KeepAlive(drains);
        }
    }

    internal static int Child(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var output = Environment.GetEnvironmentVariable("RESEARCHLAB_OUTPUT_DIR")!;
        var index = args[3];
        try
        {
            Console.WriteLine("child stdout " + args[2] + " " + index);
            Console.Error.WriteLine("child stderr " + args[2] + " " + index);
            Signal(args[1] + ".ChildReady." + index);
            Wait(args[1] + ".Release");
            return 0;
        }
        finally { File.WriteAllText(Path.Combine(output, "child-finally-" + index + ".txt"), "managed child finally " + args[2]); }
    }

    // An ordinary/console-attached parent remains alive while the supervisor kills ONLY its CLI.
    internal static async Task<int> CliParentAsync(string[] args)
    {
        var console = args[1] == "console";
        if (console)
        {
            FreeConsole();
            if (!AllocConsole()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            ShowWindow(GetConsoleWindow(), 0);
        }
        try
        {
            var start = new ProcessStartInfo(DotnetPath)
            {
                UseShellExecute = false,
                CreateNoWindow = !console,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args.Skip(4)) start.ArgumentList.Add(arg);
            using var cli = Process.Start(start)!;
            var output = cli.StandardOutput.ReadToEndAsync();
            var error = cli.StandardError.ReadToEndAsync();
            await cli.StandardInput.WriteLineAsync("yes");
            cli.StandardInput.Close();
            File.WriteAllText(args[2] + ".pid", cli.Id.ToString(CultureInfo.InvariantCulture));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            try
            {
                await cli.WaitForExitAsync(timeout.Token);
                await File.WriteAllTextAsync(args[2] + ".stdout", await output, timeout.Token);
                await File.WriteAllTextAsync(args[2] + ".stderr", await error, timeout.Token);
                Signal(args[3] + ".ParentObservedExit");
                Wait(args[3] + ".ParentRelease");
                return 0;
            }
            finally
            {
                if (!cli.HasExited) { cli.Kill(); await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        }
        finally { if (console) FreeConsole(); }
    }

    internal static string DotnetPath => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    private static void Signal(string name) { using var handle = EventWaitHandle.OpenExisting(name); handle.Set(); }
    private static void Wait(string name)
    {
        using var handle = EventWaitHandle.OpenExisting(name);
        if (!handle.WaitOne(TimeSpan.FromSeconds(45))) throw new TimeoutException("Synthetic event deadline: " + name);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
}
