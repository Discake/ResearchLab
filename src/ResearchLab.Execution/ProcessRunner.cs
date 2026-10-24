using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ResearchLab.Application;

namespace ResearchLab.Execution;

public sealed class ProcessRunner : IProcessRunner
{
    private const long OutputLimitBytes = 1024 * 1024;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> ExecuteAsync(PreparedRun launch, Func<ProcessStarted, Task> onStarted)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onStarted);
        // On Windows, batch files can trigger command-processor fallback even with
        // UseShellExecute=false. This increment accepts native .exe entry points only.
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(launch.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            return new(null, false, "Windows execution requires a native .exe entry point; script/shell fallback is not supported.");
        var start = new ProcessStartInfo(launch.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in launch.ExperimentSnapshot.Task.Arguments)
            start.ArgumentList.Add(argument);
        start.Environment["RESEARCHLAB_PARAMETERS_JSON"] = JsonSerializer.Serialize(
            launch.ParametersSnapshot.ToDictionary(pair => pair.Key, pair => pair.Value.Value));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                return new(null, false, "The operating system did not start the process.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Do not persist arbitrary exception text containing private host paths.
            return new(null, false, "The operating system could not start the declared executable.");
        }

        var started = new ProcessStarted(process.Id, DateTimeOffset.UtcNow);
        using var deadline = new CancellationTokenSource(launch.ExperimentSnapshot.Task.Timeout);
        long outputBytes = 0;
        string? drainFailure = null;
        var stdout = DrainAsync(process.StandardOutput.BaseStream);
        var stderr = DrainAsync(process.StandardError.BaseStream);
        // Observe exit immediately: a slow acknowledgement must not turn an already
        // completed process/drain into a timeout when WaitForExitAsync is first called.
        var completion = Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
        try
        {
            process.StandardInput.Close();
            await onStarted(started);
            try
            {
                await completion.WaitAsync(deadline.Token);
                return new(process.ExitCode, false, drainFailure);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                await StopAsync(process);
                return new(process.ExitCode, drainFailure is null, drainFailure);
            }
        }
        finally
        {
            // Includes failure to persist Running in the Application callback.
            deadline.Cancel();
            try { await StopAsync(process); }
            finally
            {
                // Bound cleanup even if an inherited pipe does not respond to cancellation.
                try { await completion.WaitAsync(CleanupTimeout); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
        }

        async Task DrainAsync(Stream stream)
        {
            var buffer = new byte[8192];
            try
            {
                // Cancellation is not EOF: incomplete collection must never look successful.
                int count;
                while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    if (Interlocked.Add(ref outputBytes, count) > OutputLimitBytes)
                    {
                        Interlocked.CompareExchange(ref drainFailure, "Process output exceeded the 1 MiB combined limit.", null);
                        deadline.Cancel();
                        return;
                    }
                }
            }
            catch (IOException)
            {
                Interlocked.CompareExchange(ref drainFailure, "Process output could not be drained.", null);
                deadline.Cancel();
            }
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        // Failure to stop is surfaced to the caller; never report a false terminal outcome.
        await process.WaitForExitAsync().WaitAsync(CleanupTimeout);
    }
}
