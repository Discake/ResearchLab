using ResearchLab.Domain;

namespace ResearchLab.Application;

// Caller-owned foreground lifetime. Keep this object and the driver task until completion.
public sealed class RunOperation : IDisposable
{
    private readonly object gate = new();
    private readonly Func<Task<RunId>> execute;
    private readonly IProcessExecution? process;
    private Task<RunId>? driver;
    private readonly IRunSession? session;
    private bool disposed;

    internal RunOperation(RunId id, Func<Task<RunId>> execute, IProcessExecution? process = null, IRunSession? session = null)
    {
        RunId = id;
        this.execute = execute;
        this.process = process;
        this.session = session;
    }

    public RunId RunId { get; }

    public bool RequestCancellation() => process?.RequestCancellation() ?? false;

    // No request token: this task owns execution and mandatory persistence.
    // Only unconfirmed cleanup can be retried, using the same process handle.
    public Task<RunId> ExecuteAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (driver is null || driver.Exception?.InnerException is ProcessCleanupException)
                driver = DriveAsync();
            return driver;
        }
    }

    private async Task<RunId> DriveAsync()
    {
        var retainOwnership = false;
        try { return await execute(); }
        catch (ProcessCleanupException exception)
        {
            retainOwnership = true;
            exception.Operation = this;
            throw;
        }
        finally
        {
            if (!retainOwnership)
                session?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (driver is not null && (!driver.IsCompleted || driver.Exception?.InnerException is ProcessCleanupException))
                throw new InvalidOperationException("Finish execution and confirm cleanup before releasing its session.");
            disposed = true;
            session?.Dispose();
        }
    }

    public Task<RunId> WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return (driver ?? throw new InvalidOperationException("Start the foreground driver first."))
                .WaitAsync(cancellationToken);
    }
}
