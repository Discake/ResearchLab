using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Execution;

// The narrow OS stop seam permits deterministic testing of denied/failed termination.
public interface IProcessTerminator
{
    void KillTree(Process process);
}

public sealed class ProcessRunner(RunLogFiles logs, IProcessTerminator? terminator = null,
    TimeProvider? timeProvider = null) : IProcessRunner
{
    public IProcessExecution CreateExecution(RunId runId, PreparedRun launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return new OwnedProcess(logs, runId, launch, terminator, timeProvider ?? TimeProvider.System);
    }

    // Convenience for foreground callers that do not need a cancellation handle.
    public Task<ProcessResult> ExecuteAsync(RunId runId, PreparedRun launch, Func<ProcessStarted, Task> onStarted) =>
        CreateExecution(runId, launch).ExecuteAsync(onStarted);

    private sealed class OwnedProcess(RunLogFiles logs, RunId runId, PreparedRun launch,
        IProcessTerminator? terminator, TimeProvider clock) : IProcessExecution
    {
        private const long OutputLimitBytes = 1024 * 1024;
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
        private readonly object gate = new();
        private readonly SemaphoreSlim driver = new(1);
        private readonly TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource reads = new();
        private enum Outcome { Pending, Exit, Canceled, TimedOut, Failed }
        private Outcome outcome;
        private Process? process;
        private FileStream? output;
        private FileStream? error;
        private Task? exit;
        private Task? drains;
        private ITimer? deadline;
        private bool attempted;
        private bool released;
        private long outputBytes;
        private string? drainFailure;
        private ProcessResult? result;
        private ExceptionDispatchInfo? startupFailure;

        public bool RequestCancellation()
        {
            lock (gate)
            {
                Choose(Outcome.Canceled);
                return outcome == Outcome.Canceled;
            }
        }

        // gate serializes intent and observed exit. An already exited root wins late intent.
        private void Choose(Outcome requested)
        {
            lock (gate)
            {
                if (outcome != Outcome.Pending)
                    return;
                outcome = process is not null && process.HasExited ? Outcome.Exit : requested;
                signal.TrySetResult();
            }
        }

        public async Task<ProcessResult> ExecuteAsync(Func<ProcessStarted, Task> onStarted)
        {
            ArgumentNullException.ThrowIfNull(onStarted);
            await driver.WaitAsync();
            try
            {
                if (released)
                {
                    startupFailure?.Throw();
                    return result!;
                }
                if (!attempted)
                {
                    attempted = true;
                    lock (gate)
                    {
                        if (outcome == Outcome.Canceled)
                        {
                            result = new(null, false, null, Canceled: true);
                            Release();
                            return result;
                        }
                        try
                        {
                            (output, error) = logs.Create(runId);
                            if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(launch.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Native executable required.");
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
                            var candidate = new Process { StartInfo = start };
                            try
                            {
                                if (!candidate.Start())
                                    throw new InvalidOperationException("Process did not start.");
                                process = candidate;
                            }
                            catch { candidate.Dispose(); throw; }
                        }
                        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
                        {
                            outcome = Outcome.Failed;
                            result = new(null, false, "The operating system could not prepare logs or start the declared native executable.");
                            Release();
                            return result;
                        }
                    }
                    var started = new ProcessStarted(process.Id, clock.GetUtcNow());
                    exit = process.WaitForExitAsync();
                    drains = Task.WhenAll(DrainAsync(process.StandardOutput.BaseStream, output!),
                        DrainAsync(process.StandardError.BaseStream, error!));
                    deadline = clock.CreateTimer(_ => Choose(Outcome.TimedOut), null,
                        launch.ExperimentSnapshot.Task.Timeout, Timeout.InfiniteTimeSpan);
                    try
                    {
                        process.StandardInput.Close();
                        await onStarted(started);
                    }
                    catch (Exception exception)
                    {
                        startupFailure = ExceptionDispatchInfo.Capture(exception);
                        Choose(Outcome.Failed);
                    }
                }

                // All asynchronous work belongs to this foreground driver and the owned handle.
                await Task.WhenAny(exit!, signal.Task);
                Choose(Outcome.Exit);
                try
                {
                    if (!process!.HasExited)
                    {
                        try
                        {
                            if (terminator is null)
                                process.Kill(entireProcessTree: true);
                            else
                                terminator.KillTree(process);
                        }
                        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
                        {
                            if (!process.HasExited)
                                throw new ProcessCleanupException(this);
                        }
                    }
                    await exit!.WaitAsync(CleanupTimeout);
                }
                catch (TimeoutException) { throw new ProcessCleanupException(this); }
                // The root is confirmed dead. Pipe collection is separately bounded.
                try { await drains!.WaitAsync(CleanupTimeout); }
                catch (TimeoutException)
                {
                    Interlocked.CompareExchange(ref drainFailure, "Process output collection did not complete within the cleanup limit.", null);
                    reads.Cancel();
                    // Close pipe handles so no pending read is left using the log files.
                    process!.StandardOutput.Close();
                    process.StandardError.Close();
                    throw new ProcessCleanupException(this);
                }
                result = new(process!.ExitCode, outcome == Outcome.TimedOut && drainFailure is null,
                    drainFailure, outcome == Outcome.Canceled && drainFailure is null);
                Release();
                startupFailure?.Throw();
                return result;
            }
            catch (Exception exception) when (exception is not ProcessCleanupException && process is not null && !released)
            {
                startupFailure ??= ExceptionDispatchInfo.Capture(exception);
                Choose(Outcome.Failed);
                throw new ProcessCleanupException(this);
            }
            finally { driver.Release(); }
        }

        private async Task DrainAsync(Stream stream, Stream destination)
        {
            var buffer = new byte[8192];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, reads.Token)) != 0)
                {
                    var total = Interlocked.Add(ref outputBytes, count);
                    var retained = (int)Math.Clamp(OutputLimitBytes - (total - count), 0, count);
                    if (retained > 0)
                        await destination.WriteAsync(buffer.AsMemory(0, retained), reads.Token);
                    if (total > OutputLimitBytes)
                    {
                        Interlocked.CompareExchange(ref drainFailure, "Process output exceeded the 1 MiB combined limit.", null);
                        Choose(Outcome.Failed);
                        return;
                    }
                }
                await destination.FlushAsync(reads.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
            {
                Interlocked.CompareExchange(ref drainFailure, "Process output could not be read or saved.", null);
                Choose(Outcome.Failed);
            }
        }

        private void Release()
        {
            // Reached only before startup or after exit and settled collection.
            released = true;
            deadline?.Dispose();
            try { output?.Dispose(); }
            finally
            {
                try { error?.Dispose(); }
                finally
                {
                    process?.Dispose();
                    reads.Dispose();
                }
            }
        }
    }
}
