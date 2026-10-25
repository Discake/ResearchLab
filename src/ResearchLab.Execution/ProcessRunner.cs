using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Execution;

// The narrow OS stop seam permits deterministic testing of denied/failed termination.
public interface IProcessTerminator
{
    void Stop(Action stopGroup);
}

public sealed class ProcessRunner(RunLogFiles logs, IProcessTerminator? terminator = null,
    TimeProvider? timeProvider = null) : IProcessRunner
{
    internal IWindowsJobControl JobControl { get; init; } = new WindowsJobControl();
    internal Func<Stream, Stream>? LogWriter { get; init; }

    public IProcessExecution CreateExecution(RunId runId, PreparedRun launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return new OwnedProcess(logs, runId, launch, terminator, timeProvider ?? TimeProvider.System, JobControl, LogWriter);
    }

    // Convenience for foreground callers that do not need a cancellation handle.
    public Task<ProcessResult> ExecuteAsync(RunId runId, PreparedRun launch, Func<ProcessStarted, Task> onStarted) =>
        CreateExecution(runId, launch).ExecuteAsync(onStarted);

    private sealed class OwnedProcess(RunLogFiles logs, RunId runId, PreparedRun launch,
        IProcessTerminator? terminator, TimeProvider clock, IWindowsJobControl jobs,
        Func<Stream, Stream>? logWriter) : IProcessExecution
    {
        private const long OutputLimitBytes = 1024 * 1024;
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
        private readonly object gate = new();
        private readonly SemaphoreSlim driver = new(1);
        private readonly TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource reads = new();
        private enum Outcome { Pending, Exit, Canceled, TimedOut, Failed }
        private Outcome outcome;
        private IRunProcess? process;
        private Stream? output;
        private Stream? error;
        private Task? drains;
        private ITimer? deadline;
        private bool attempted;
        private bool released;
        private long outputBytes;
        private string? drainFailure;
        private string? groupFailure;
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

        // gate serializes intent and observed completion. On Windows root exit alone cannot win.
        private void Choose(Outcome requested)
        {
            lock (gate)
            {
                if (outcome != Outcome.Pending)
                    return;
                outcome = process is not null && ObserveCompletion() ? Outcome.Exit :
                    groupFailure is not null ? Outcome.Failed : requested;
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
                            var outputDirectory = logs.CreateOutputDirectory(runId);
                            if (logWriter is not null)
                            {
                                output = logWriter(output);
                                error = logWriter(error);
                            }
                            if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(launch.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Native executable required.");
                            var parametersJson = JsonSerializer.Serialize(
                                launch.ParametersSnapshot.ToDictionary(pair => pair.Key, pair => pair.Value.Value));
                            if (OperatingSystem.IsWindows())
                            {
                                var candidate = new WindowsRunProcess(jobs);
                                candidate.Start(launch, parametersJson, outputDirectory);
                                process = candidate;
                            }
                            else
                                process = new LocalRunProcess(launch, parametersJson, outputDirectory);
                        }
                        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
                        {
                            outcome = Outcome.Failed;
                            result = new(null, false, OperatingSystem.IsWindows()
                                ? "Windows could not prepare run storage or create the native executable inside a required Job Object; no legacy fallback is permitted."
                                : "The operating system could not prepare run storage or start the declared native executable.");
                            Release();
                            return result;
                        }
                    }
                    Task? stdoutDrain = null;
                    Task? stderrDrain = null;
                    try
                    {
                        var started = new ProcessStarted(process.Id, clock.GetUtcNow());
                        stdoutDrain = DrainAsync(process.Stdout, output!);
                        stderrDrain = DrainAsync(process.Stderr, error!);
                        deadline = clock.CreateTimer(_ => Choose(Outcome.TimedOut), null,
                            launch.ExperimentSnapshot.Task.Timeout, Timeout.InfiniteTimeSpan);
                        process.CloseInput();
                        await onStarted(started);
                    }
                    catch (Exception exception)
                    {
                        startupFailure = ExceptionDispatchInfo.Capture(exception);
                        Choose(Outcome.Failed);
                    }
                    finally { drains = Task.WhenAll(stdoutDrain ?? Task.CompletedTask, stderrDrain ?? Task.CompletedTask); }
                }

                // One foreground driver; no detached wait retaining a native process reference.
                while (true)
                {
                    lock (gate)
                    {
                        if (outcome != Outcome.Pending) break;
                        if (ObserveCompletion()) outcome = Outcome.Exit;
                        else if (groupFailure is not null) outcome = Outcome.Failed;
                        if (outcome != Outcome.Pending) break;
                    }
                    await Task.WhenAny(Task.Delay(10), signal.Task);
                }
                await ConfirmCleanupAsync();
                // Root and (on Windows) Job drain are confirmed. Collection is separately bounded.
                try { await drains!.WaitAsync(CleanupTimeout); }
                catch (TimeoutException)
                {
                    Interlocked.CompareExchange(ref drainFailure, "Process output collection did not complete within the cleanup limit.", null);
                    reads.Cancel();
                    // Close pipe handles so no pending read is left using the log files.
                    process!.Stdout.Close();
                    process.Stderr.Close();
                    throw new ProcessCleanupException(this);
                }
                var failure = drainFailure ?? groupFailure;
                result = new(process!.ExitCode, outcome == Outcome.TimedOut && failure is null,
                    failure, outcome == Outcome.Canceled && failure is null);
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

        // Must hold gate: root observation may cache exit code and close its native handle.
        private bool ObserveCompletion()
        {
            try { return process!.IsComplete; }
            catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                groupFailure ??= "Process group completion could not be queried reliably.";
                return false;
            }
        }

        private async Task ConfirmCleanupAsync()
        {
            lock (gate)
            {
                if (ObserveCompletion()) return;
                try
                {
                    if (terminator is null) process!.Stop();
                    else terminator.Stop(process!.Stop);
                }
                catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (!ObserveCompletion()) throw new ProcessCleanupException(this);
                }
            }
            // TerminateJobObject is a request, not confirmation. Retry retains this same owner.
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            try
            {
                while (true)
                {
                    lock (gate) { if (ObserveCompletion()) return; }
                    await Task.Delay(10, timeout.Token);
                }
            }
            catch (OperationCanceledException) { throw new ProcessCleanupException(this); }
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
