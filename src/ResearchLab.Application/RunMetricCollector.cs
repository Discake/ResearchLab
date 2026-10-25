using System.Runtime.ExceptionServices;
using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed record ProtocolDiagnostic(long Line, string Code);

// The runner awaits each call serially. Ordinary lines may arrive before acknowledgement.
// The sink must await the acknowledgement result before consuming a metric.
// Memory is borrowed for the duration of the call; do not retain it.
public interface IProcessOutputSink
{
    Task<IReadOnlyList<ProtocolDiagnostic>> OnStdoutLineAsync(ReadOnlyMemory<byte> line, long lineNumber,
        Task<bool> runningAcknowledged);
    Task<IReadOnlyList<ProtocolDiagnostic>> CompleteStdoutAsync();
}

// One sequential stdout owner; parsing and database writes stay inside Application.
internal sealed class RunMetricCollector(IResearchStore store, RunId id, TimeProvider clock) : IProcessOutputSink
{
    private readonly List<long> pendingLines = [];
    public RunMetricIngestion? Ingestion { get; private set; }
    public ExceptionDispatchInfo? Failure { get; private set; }

    public async Task<IReadOnlyList<ProtocolDiagnostic>> OnStdoutLineAsync(ReadOnlyMemory<byte> line, long lineNumber,
        Task<bool> runningAcknowledged)
    {
        try
        {
            switch (MetricProtocol.Parse(line.Span))
            {
                case MetricParseResult.Error error:
                    return [new(lineNumber, error.Code.ToString())];
                case MetricParseResult.Metric metric:
                    if (!await runningAcknowledged) return [];
                    Ingestion ??= await RunMetricIngestion.OpenAsync(store, id, timeProvider: clock);
                    pendingLines.Add(lineNumber);
                    var flush = await Ingestion.AcceptAsync(metric.Event);
                    return flush is null ? [] : Describe(flush);
                default:
                    return [];
            }
        }
        catch (Exception exception)
        {
            Failure ??= ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProtocolDiagnostic>> CompleteStdoutAsync()
    {
        try { return Ingestion is null ? [] : Describe(await Ingestion.FlushAsync()); }
        catch (Exception exception)
        {
            Failure ??= ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    private IReadOnlyList<ProtocolDiagnostic> Describe(MetricFlushResult flush)
    {
        var diagnostics = flush.Rejections.Select(rejection =>
            new ProtocolDiagnostic(pendingLines[rejection.Index], rejection.Code)).ToArray();
        pendingLines.Clear();
        return diagnostics;
    }
}
