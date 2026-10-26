using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed record MetricPointDetails(string Name, double Value, long? Step, string? Unit,
    DateTimeOffset RecordedAtUtc, long Sequence);

public sealed record RunMetricPage(RunId RunId, string? Name, int Offset, int PageSize,
    bool HasMore, IReadOnlyList<MetricPointDetails> Points);

public interface IRunMetricReader
{
    Task<RunMetricPage> GetMetricsAsync(RunId runId, string? name = null, RunPageRequest? page = null,
        CancellationToken cancellationToken = default);
}
