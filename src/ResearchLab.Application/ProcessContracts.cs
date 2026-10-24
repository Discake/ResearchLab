using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed record ProcessStarted(int ProcessId, DateTimeOffset StartedAtUtc);

// A null exit code is also used when no OS process was started.
public sealed record ProcessResult(int? ExitCode, bool TimedOut, string? FailureReason);

public interface IProcessRunner
{
    // Invoke onStarted only after OS startup; await it before reporting completion.
    // If it throws, stop the owned process and propagate the error, never relaunch.
    Task<ProcessResult> ExecuteAsync(RunId runId, PreparedRun launch, Func<ProcessStarted, Task> onStarted);
}
