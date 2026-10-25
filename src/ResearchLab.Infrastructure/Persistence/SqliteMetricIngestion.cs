using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Persistence;

public sealed partial class SqliteResearchStore
{
    public async Task<IRunMetricWrite> OpenRunMetricWriteAsync(RunId id, IReadOnlyCollection<string> names,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count > RunMetricIngestion.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(names));
        var db = new ResearchDbContext(options);
        try
        {
            var run = await db.Set<Run>().SingleOrDefaultAsync(value => value.Id == id, cancellationToken)
                ?? throw new CatalogException("RunNotFound", "Run was not found.");
            if (expectedVersion.HasValue && run.Version != expectedVersion)
                throw new CatalogException("VersionConflict", "Another writer has updated this Run.");
            if (run.Status != RunStatus.Running)
                throw new CatalogException("InvalidMetricState", "Metric ingestion requires a Running Run.");
            var sequence = await db.Set<MetricPoint>().Where(point => point.RunId == id)
                .Select(point => (long?)point.Sequence).MaxAsync(cancellationToken) ?? 0;
            var units = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                // One indexed seek per series, independent of the number of observations.
                var point = await db.Set<MetricPoint>().AsNoTracking()
                    .Where(point => point.RunId == id && point.Name == name)
                    .OrderBy(point => point.Sequence).Select(point => new { point.Unit })
                    .FirstOrDefaultAsync(cancellationToken);
                if (point is not null)
                    units.Add(name, point.Unit);
            }
            run.InitializeMetricWindow(sequence, names.Distinct(StringComparer.Ordinal).ToArray(), units,
                RunMetricIngestion.MaxBatchSize);
            return new MetricWrite(db, run);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private sealed class MetricWrite(ResearchDbContext db, Run run) : IRunMetricWrite
    {
        private bool saved;
        public Run Run { get; } = run;

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            if (saved)
                throw new InvalidOperationException("A metric write window can only be saved once.");
            saved = true;
            foreach (var point in Run.Metrics)
                db.Entry(point).State = EntityState.Added;
            try
            {
                // SaveChanges wraps INSERTs and the concurrency-checked Run UPDATE in one transaction.
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new CatalogException("VersionConflict", "Another writer has updated this Run; batch was not saved.");
            }
            catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
            {
                var current = await db.Set<Run>().AsNoTracking().Where(value => value.Id == Run.Id)
                    .Select(value => value.Version).SingleAsync(cancellationToken);
                if (current != db.Entry(Run).Property(value => value.Version).OriginalValue)
                    throw new CatalogException("VersionConflict", "Another writer has updated this Run; batch was not saved.");
                throw;
            }
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
