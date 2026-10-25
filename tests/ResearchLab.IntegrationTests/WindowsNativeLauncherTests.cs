using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResearchLab.Execution;
using Xunit;
using static ResearchLab.Execution.WindowsNativeMethods;

namespace ResearchLab.IntegrationTests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) Skip = "Requires real Windows 10+ Job Objects.";
    }
}

public sealed class WindowsNativeLauncherTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ResearchLab.ProcessFixture.exe");

    [WindowsFact]
    public async Task NativeFixtureSucceedsAndReportsNonzeroAndStillActiveExitCodes()
    {
        using var fixture = new TestWorkspace();
        foreach (var (mode, expected) in new[] { ("success", 0), ("fail", 7), ("native-exit-259", 259) })
        {
            using var process = Launch(fixture, [mode]);
            var stdout = Drain(process.StandardOutput);
            var stderr = Drain(process.StandardError);
            Assert.Equal(expected, await Exit(process));
            await Task.WhenAll(stdout, stderr).WaitAsync(Limit);
            if (expected == 7) Assert.Contains("Synthetic failure.", await stderr);
        }
    }

    [WindowsFact]
    public async Task SpacePathUnicodeArgumentsEnvironmentCwdAndSeparateStreamsRoundTrip()
    {
        using var fixture = new TestWorkspace();
        var directory = Path.Combine(fixture.Root, "native fixture с пробелами");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Executable)!))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        string[] arguments = ["", "Привет 世界 😀", "a b", "a\tb", "\"", "a\"b", "\\", "a b\\", "\\\\\"", "{\"n\":\"тест\\\\\"}",
            "$(command)", "; echo forbidden", "%PATH%", "line\nend", "trailing\\\\"];
        var json = "{\"value\":\"Unicode Привет 😀\"}";
        var probeArguments = new List<string> { "native-echo", "0" };
        probeArguments.AddRange(arguments);
        var launcher = new WindowsNativeProcessLauncher(job =>
            probeArguments[1] = job.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture));
        using var process = launcher.Launch(Path.Combine(directory, Path.GetFileName(Executable)),
            probeArguments, fixture.Workspace, json, directory);
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        Assert.Equal(0, await Exit(process));
        using var result = JsonDocument.Parse(await stdout.WaitAsync(Limit));
        var root = result.RootElement;
        Assert.Equal(arguments, root.GetProperty("Arguments").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(fixture.Workspace, root.GetProperty("Cwd").GetString());
        Assert.Equal(json, root.GetProperty("Parameters").GetString());
        Assert.Equal(directory, root.GetProperty("Output").GetString());
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), root.GetProperty("Inherited").GetString());
        Assert.Equal("", root.GetProperty("Stdin").GetString());
        Assert.False(root.GetProperty("InheritedJob").GetBoolean());
        Assert.Equal(process.ProcessId, root.GetProperty("Pid").GetInt32());
        Assert.Equal("synthetic stderr: Привет\n", await stderr.WaitAsync(Limit));
    }

    [WindowsFact]
    public async Task HeldProcessBelongsToSpecificJobWithNoBreakaway()
    {
        using var fixture = new TestWorkspace();
        using var process = Launch(fixture, ["native-hold", Path.Combine(fixture.Root, "ready")]);
        await Ready(Path.Combine(fixture.Root, "ready"));
        Assert.True(IsProcessInJob(process.Process, process.Job, out var member));
        Assert.True(member);
        var limits = new ExtendedLimits();
        Assert.True(QueryInformationJobObject(process.Job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out _));
        Assert.Equal(0x2000u, limits.Basic.Flags);
        Assert.True(GetHandleInformation(process.Job, out var flags));
        Assert.Equal(0u, flags & 1);
        process.Job.Dispose();
        await Exit(process);
    }

    [WindowsFact]
    public void RealJobPreparationFailurePreventsExecutableFromRunning()
    {
        using var fixture = new TestWorkspace();
        var marker = Path.Combine(fixture.Root, "must-not-exist");
        var launcher = new WindowsNativeProcessLauncher(job =>
        {
            var invalid = new ExtendedLimits();
            WindowsNativeProcessLauncher.Check(SetInformationJobObject(job, 9, ref invalid, 0));
        });
        Assert.Throws<Win32Exception>(() => launcher.Launch(Executable, ["native-marker", marker], fixture.Workspace, "{}", fixture.Root));
        Assert.False(File.Exists(marker));
    }

    [WindowsFact]
    public async Task CreateProcessFailureHasNoLiveProcessAndConcurrentLaunchesReleaseResources()
    {
        using var fixture = new TestWorkspace();
        var bad = Path.Combine(fixture.Root, "invalid executable.exe");
        File.WriteAllText(bad, "Synthetic invalid PE file.");
        var exception = Assert.Throws<Win32Exception>(() => new WindowsNativeProcessLauncher().Launch(bad, [], fixture.Workspace, "{}", fixture.Root));
        Assert.Contains(exception.NativeErrorCode, new[] { 193, 216 }); // Bad PE / machine type mismatch: no executable ran.
        using var probe = Launch(fixture, ["native-resources", bad]);
        var output = Drain(probe.StandardOutput);
        var error = Drain(probe.StandardError);
        var code = await Exit(probe);
        Assert.True(code == 0, $"Resource probe exit {code}; stdout: {await output.WaitAsync(Limit)}; stderr: {await error.WaitAsync(Limit)}");
    }

    [WindowsFact]
    public async Task ForcedOwnerDeathKillsRootAndDescendant()
    {
        using var fixture = new TestWorkspace();
        var ready = Path.Combine(fixture.Root, "descendant-ready");
        using var owner = Process.Start(new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = fixture.Workspace,
            ArgumentList = { "native-owner", ready }
        })!;
        Process? root = null;
        Process? child = null;
        var errors = owner.StandardError.ReadToEndAsync();
        try
        {
            var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(Limit);
            Assert.False(string.IsNullOrEmpty(line), owner.HasExited ? await errors.WaitAsync(Limit) : "No readiness acknowledgement.");
            var ids = line!.Split(' ').Select(int.Parse).ToArray();
            root = Process.GetProcessById(ids[0]);
            child = Process.GetProcessById(ids[1]);
            Assert.False(root.HasExited);
            Assert.False(child.HasExited);
            owner.Kill(); // ONLY the owner; Job must terminate its own processes.
            await owner.WaitForExitAsync().WaitAsync(Limit);
            await Task.WhenAll(root.WaitForExitAsync(), child.WaitForExitAsync()).WaitAsync(Limit);
            Assert.True(root.HasExited);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(true); await owner.WaitForExitAsync().WaitAsync(Limit); }
            foreach (var process in new[] { root, child })
            {
                if (process is null) continue;
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(Limit); }
                process.Dispose();
            }
        }
    }

    [WindowsFact]
    public async Task ConcurrentLaunchesHaveIndependentJobsHandlesAndEof()
    {
        using var fixture = new TestWorkspace();
        using var held = Launch(fixture, ["native-hold", Path.Combine(fixture.Root, "held-ready")]);
        await Ready(Path.Combine(fixture.Root, "held-ready"));
        using var second = await Task.Run(() => Launch(fixture, ["native-hold", Path.Combine(fixture.Root, "second-ready")]));
        await Ready(Path.Combine(fixture.Root, "second-ready"));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            using var process = Launch(fixture, ["native-echo", "0", i.ToString(CultureInfo.InvariantCulture)]);
            Assert.True(IsProcessInJob(process.Process, process.Job, out var own));
            Assert.True(own);
            Assert.True(IsProcessInJob(process.Process, held.Job, out var other));
            Assert.False(other);
            var stdout = Drain(process.StandardOutput);
            var stderr = Drain(process.StandardError);
            Assert.Equal(0, await Exit(process));
            using var result = JsonDocument.Parse(await stdout.WaitAsync(Limit));
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), result.RootElement.GetProperty("Arguments")[0].GetString());
            await stderr.WaitAsync(Limit);
        }))).WaitAsync(Limit);
        Assert.Equal(258u, WaitForSingleObject(held.Process, 0));
        held.Job.Dispose();
        await Exit(held);
        Assert.Equal(258u, WaitForSingleObject(second.Process, 0));
        // The second run must not retain a duplicate of the first run's write end.
        Assert.Equal("", await Drain(held.StandardOutput).WaitAsync(Limit));
        Assert.Equal("", await Drain(held.StandardError).WaitAsync(Limit));
        second.Job.Dispose();
        await Exit(second);
    }

    [WindowsFact]
    public async Task BothLargeStreamsDrainAndPendingAsyncReadCanBeCanceled()
    {
        using var fixture = new TestWorkspace();
        using (var process = Launch(fixture, ["streams"]))
        {
            var output = Drain(process.StandardOutput);
            var error = Drain(process.StandardError);
            Assert.Equal(0, await Exit(process));
            Assert.Equal(256 * 1024, (await output.WaitAsync(Limit)).Length);
            Assert.Equal(256 * 1024, (await error.WaitAsync(Limit)).Length);
        }
        using var held = Launch(fixture, ["native-hold", Path.Combine(fixture.Root, "read-ready")]);
        await Ready(Path.Combine(fixture.Root, "read-ready"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await held.StandardOutput.ReadAsync(new byte[1], cancellation.Token).AsTask().WaitAsync(Limit));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held.WaitForRootExitAsync(cancellation.Token));
        held.Job.Dispose();
        await Exit(held);
    }

    private static WindowsNativeProcess Launch(TestWorkspace fixture, string[] args) =>
        new WindowsNativeProcessLauncher().Launch(Executable, args, fixture.Workspace, "{}", fixture.Root);
    private static Task<string> Drain(Stream stream) => new StreamReader(stream).ReadToEndAsync();
    private static async Task<int> Exit(WindowsNativeProcess process)
    {
        using var timeout = new CancellationTokenSource(Limit);
        return await process.WaitForRootExitAsync(timeout.Token);
    }
    private static async Task Ready(string path)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (true)
        {
            try { if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, timeout.Token), out _)) return; }
            catch (IOException) { }
            await Task.Delay(10, timeout.Token);
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(KernelHandle job, int informationClass,
        ref ExtendedLimits information, uint size, out uint returned);
}
