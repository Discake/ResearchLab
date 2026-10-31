using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed record MetricRejection(int Index, string Code);
public sealed record MetricFlushResult(int SavedCount, IReadOnlyList<MetricRejection> Rejections, long Version);

/// <summary>One Application owner per Running Run. Calls must be awaited serially.</summary>
public sealed class RunMetricIngestion
{
    public const int MaxBatchSize = 128;
    private readonly IResearchStore store;
    private readonly RunId id;
    private readonly TimeProvider clock;
    private readonly int batchSize;
    private readonly TimeSpan interval;
    private readonly List<(MetricEvent Event, DateTimeOffset At)> pending = [];
    private long lastFlush;
    private int busy;
    private bool stopped;

    private RunMetricIngestion(IResearchStore store, RunId id, long version, int batchSize,
        TimeSpan interval, TimeProvider clock)
    {
        this.store = store;
        this.id = id;
        Version = version;
        this.batchSize = batchSize;
        this.interval = interval;
        this.clock = clock;
        lastFlush = clock.GetTimestamp();
    }

    public long Version { get; private set; }
    public int PendingCount => pending.Count;

    public static async Task<RunMetricIngestion> OpenAsync(IResearchStore store, RunId id,
        int batchSize = MaxBatchSize, TimeSpan? flushInterval = null, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        var interval = flushInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        await using var write = await store.OpenRunMetricWriteAsync(id, [], cancellationToken: cancellationToken);
        return new RunMetricIngestion(store, id, write.Run.Version, batchSize, interval, timeProvider ?? TimeProvider.System);
    }

    // null means buffered, NOT durable. Rejections are reported by the next successful flush,
    // indexed within that batch. The caller must consume auto-flush results as well.
    public async Task<MetricFlushResult?> AcceptAsync(MetricEvent metric, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metric);
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending.Add((metric, clock.GetUtcNow()));
            return pending.Count >= batchSize || clock.GetElapsedTime(lastFlush) >= interval
                ? await FlushCoreAsync(cancellationToken) : null;
        }
        finally { Exit(); }
    }

    public async Task<MetricFlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        Enter();
        try { return await FlushCoreAsync(cancellationToken); }
        finally { Exit(); }
    }

    // The Application, never the process adapter, chooses the terminal transition.
    // Flush is a separate durable commit: valid evidence survives a failed terminal write.
    public async Task<MetricFlushResult> CompleteAsync(ProcessResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        Enter();
        try
        {
            var flush = await FlushCoreAsync(cancellationToken);
            await using var write = await store.OpenRunMetricWriteAsync(id, [], Version, cancellationToken);
            var run = write.Run;
            var at = clock.GetUtcNow();
            if (result.Canceled && result.FailureReason is null)
                run.Cancel(at, run.Version);
            else if (result.TimedOut)
                run.TimeOut(at, run.Version);
            else if (result.FailureReason is not null || result.ExitCode != 0)
                run.Fail(at, result.FailureReason ?? $"Process exited with code {result.ExitCode}.", run.Version, result.ExitCode);
            else
                run.Succeed(at, 0, resultsValidated: true, run.Version);
            await write.SaveAsync(cancellationToken);
            Version = run.Version;
            stopped = true;
            return flush with { Version = Version };
        }
        catch { stopped = true; throw; }
        finally { Exit(); }
    }

    private async Task<MetricFlushResult> FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
            return new MetricFlushResult(0, [], Version);
        try
        {
            var names = pending.Select(item => item.Event.Name).Distinct(StringComparer.Ordinal).ToArray();
            await using var write = await store.OpenRunMetricWriteAsync(id, names, Version, cancellationToken);
            var rejected = new List<MetricRejection>();
            for (var index = 0; index < pending.Count; index++)
            {
                var (metric, at) = pending[index];
                try
                {
                    write.Run.AddMetric(metric.Name, metric.Value, at, write.Run.Version, metric.Step, metric.Unit);
                }
                catch (DomainException exception) when (exception.Code is "IncompatibleMetricUnit" or "NonFiniteMetric" or "Required")
                { rejected.Add(new MetricRejection(index, exception.Code)); }
            }
            var count = write.Run.Metrics.Count;
            if (count > 0)
                await write.SaveAsync(cancellationToken);
            Version = write.Run.Version;
            pending.Clear();
            lastFlush = clock.GetTimestamp();
            return new MetricFlushResult(count, rejected.AsReadOnly(), Version);
        }
        catch { stopped = true; throw; }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("Await each ingestion operation before starting another.");
        if (stopped)
        {
            Exit();
            throw new InvalidOperationException("Ingestion is completed or faulted; reconcile durable state before opening a new owner.");
        }
    }

    private void Exit() => Volatile.Write(ref busy, 0);
}
