using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed record ProcessStarted(int ProcessId, DateTimeOffset StartedAtUtc);

// A null exit code is also used when no OS process was started.
public sealed record ProcessResult(int? ExitCode, bool TimedOut, string? FailureReason, bool Canceled = false);

// No terminal evidence: the same owner must retain this execution and retry cleanup.
public sealed class ProcessCleanupException(IProcessExecution execution) : Exception("Process termination or resource cleanup could not be confirmed; execution ownership is retained.")
{
    public IProcessExecution Execution { get; } = execution;
    public RunOperation? Operation { get; internal set; }
}

public interface IProcessExecution
{
    // One foreground driver. A retry after ProcessCleanupException resumes cleanup, never launch.
    // Await onStarted before returning a result. Results/ordinary exceptions guarantee no
    // live execution within the backend's owned boundary and settled collection. Otherwise
    // throw ProcessCleanupException, retaining ownership and pending I/O. Never swallow a
    // failed acknowledgement. Execution defines platform-specific containment guarantees.
    Task<ProcessResult> ExecuteAsync(Func<ProcessStarted, Task> onStarted);
    // Thread-safe, idempotent control intent, not confirmation of termination.
    bool RequestCancellation();
}

public interface IProcessRunner
{
    // Create an owner only; no OS startup or I/O. Each owner launches at most once.
    IProcessExecution CreateExecution(RunId runId, PreparedRun launch);
}
