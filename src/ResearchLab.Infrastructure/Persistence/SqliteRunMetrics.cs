using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Persistence;

public sealed partial class SqliteResearchStore : IRunMetricReader
{
    public async Task<RunMetricPage> GetMetricsAsync(RunId runId, string? name = null,
        RunPageRequest? page = null, CancellationToken cancellationToken = default)
    {
        page ??= new RunPageRequest();
        await using var db = new ResearchDbContext(options);
        if (!await db.Set<Run>().AsNoTracking().AnyAsync(run => run.Id == runId, cancellationToken))
            throw new CatalogException("RunNotFound", "Run was not found.");
        var query = db.Set<MetricPoint>().AsNoTracking().Where(point => point.RunId == runId);
        if (name is not null) query = query.Where(point => point.Name == name);
        var points = await query.OrderBy(point => point.Sequence).Skip(page.Offset).Take(page.PageSize + 1)
            .Select(point => new MetricPointDetails(point.Name, point.Value, point.Step, point.Unit,
                point.RecordedAtUtc.ToUniversalTime(), point.Sequence)).ToListAsync(cancellationToken);
        var hasMore = points.Count > page.PageSize;
        if (hasMore) points.RemoveAt(points.Count - 1);
        return new(runId, name, page.Offset, page.PageSize, hasMore, points.AsReadOnly());
    }
}
