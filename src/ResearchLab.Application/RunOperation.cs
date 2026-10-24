using ResearchLab.Domain;

namespace ResearchLab.Application;

// Caller-owned foreground lifetime. Keep this object and the driver task until completion.
public sealed class RunOperation
{
    private readonly object gate = new();
    private readonly Func<Task<RunId>> execute;
    private readonly IProcessExecution? process;
    private Task<RunId>? driver;

    internal RunOperation(RunId id, Func<Task<RunId>> execute, IProcessExecution? process = null)
    {
        RunId = id;
        this.execute = execute;
        this.process = process;
    }

    public RunId RunId { get; }

    public bool RequestCancellation() => process?.RequestCancellation() ?? false;

    // No request token: this task owns execution and mandatory persistence.
    // Only unconfirmed cleanup can be retried, using the same process handle.
    public Task<RunId> ExecuteAsync()
    {
        lock (gate)
        {
            if (driver is null || driver.Exception?.InnerException is ProcessCleanupException)
                driver = DriveAsync();
            return driver;
        }
    }

    private async Task<RunId> DriveAsync()
    {
        try { return await execute(); }
        catch (ProcessCleanupException exception)
        {
            exception.Operation = this;
            throw;
        }
    }

    public Task<RunId> WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return (driver ?? throw new InvalidOperationException("Start the foreground driver first."))
                .WaitAsync(cancellationToken);
    }
}
