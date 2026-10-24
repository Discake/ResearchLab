using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Persistence;

public sealed class SqliteResearchStore : IResearchStore
{
    private readonly DbContextOptions<ResearchDbContext> options;

    public SqliteResearchStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        var connection = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false };
        options = new DbContextOptionsBuilder<ResearchDbContext>().UseSqlite(connection.ToString()).Options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task AddProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        db.Add(project);
        db.Entry(project).Property("WorkspaceKey").CurrentValue = OperatingSystem.IsWindows()
            ? project.WorkspacePath.ToUpperInvariant() : project.WorkspacePath;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        { throw new CatalogException("ProjectAlreadyImported", "This canonical workspace is already imported."); }
    }

    public async Task<Project?> GetProjectAsync(ProjectId id, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await db.Set<Project>().AsNoTracking().OrderBy(value => value.Name).ThenBy(value => value.Id).ToListAsync(cancellationToken);
    }

    public async Task AddExperimentAsync(Experiment experiment, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        db.Add(experiment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Experiment?> GetExperimentAsync(ExperimentId id, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await db.Set<Experiment>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Experiment>> ListExperimentsAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await db.Set<Experiment>().AsNoTracking().Where(value => value.ProjectId == projectId).OrderBy(value => value.Id).ToListAsync(cancellationToken);
    }

    public async Task AddRunAsync(Run run, CancellationToken cancellationToken = default)
    {
        if (run.Status != RunStatus.Created || run.Version != 0)
            throw new CatalogException("RunMustBeCreated", "Persist a new Run before recording execution.");
        await using var db = new ResearchDbContext(options);
        var experiment = await db.Set<Experiment>().FindAsync([run.ExperimentId], cancellationToken);
        if (experiment is null || experiment.ProjectId != run.ExperimentSnapshot.ProjectId)
            throw new CatalogException("ExperimentNotFound", "Run must belong to a persisted experiment in the same project.");
        db.Add(run);
        db.Entry(run).Property("ExperimentKey").CurrentValue = run.ExperimentId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Run?> GetRunAsync(RunId id, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await LoadRun(db, id, cancellationToken);
    }

    public async Task SaveRunAsync(Run run, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        var stored = await LoadRun(db, run.Id, cancellationToken) ?? throw new CatalogException("RunNotFound", "Run was not found.");
        if (stored.Version != expectedVersion)
            throw new CatalogException("VersionConflict", "Another writer has updated this Run.");
        if (stored.Status is not (RunStatus.Created or RunStatus.Running) || run.Version <= expectedVersion)
            throw new CatalogException("ImmutableRun", "Terminal Runs and unchanged/older versions cannot be overwritten.");
        if (stored.CreatedAtUtc != run.CreatedAtUtc ||
            SnapshotJson.WriteExperiment(stored.ExperimentSnapshot) != SnapshotJson.WriteExperiment(run.ExperimentSnapshot) ||
            SnapshotJson.WriteParameters(stored.ParametersSnapshot) != SnapshotJson.WriteParameters(run.ParametersSnapshot) ||
            stored.SourceSnapshot != run.SourceSnapshot ||
            SnapshotJson.WriteEnvironment(stored.EnvironmentSnapshot) != SnapshotJson.WriteEnvironment(run.EnvironmentSnapshot))
            throw new CatalogException("ImmutableSnapshot", "Run input snapshots cannot be replaced.");
        if (run.Metrics.Count < stored.Metrics.Count || stored.Metrics.Where((point, index) => !SameMetric(point, run.Metrics[index])).Any())
            throw new CatalogException("ImmutableMetric", "Previously recorded metrics cannot be changed or removed.");
        if (stored.StartedAtUtc.HasValue && (stored.StartedAtUtc != run.StartedAtUtc || stored.ProcessId != run.ProcessId))
            throw new CatalogException("ImmutableProcess", "Recorded process identity and start time cannot be replaced.");

        db.Attach(run);
        var entry = db.Entry(run);
        entry.Property("ExperimentKey").CurrentValue = run.ExperimentId;
        entry.Property(value => value.Version).OriginalValue = expectedVersion;
        foreach (var name in new[] { "Status", "Version", "StartedAtUtc", "FinishedAtUtc", "ProcessId", "ExitCode", "FailureReason", "lastTransitionAt" })
            entry.Property(name).IsModified = true;
        foreach (var metric in run.Metrics.Skip(stored.Metrics.Count))
            db.Entry(metric).State = EntityState.Added;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        { throw new CatalogException("VersionConflict", "Another writer has updated this Run; changes were not saved."); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // A concurrent metric INSERT can encounter the unique sequence index before
            // EF reaches the version-checked UPDATE. SaveChanges has rolled back its batch.
            var currentVersion = await db.Set<Run>().AsNoTracking().Where(value => value.Id == run.Id)
                .Select(value => value.Version).SingleAsync(cancellationToken);
            if (currentVersion != expectedVersion)
                throw new CatalogException("VersionConflict", "Another writer has updated this Run; changes were not saved.");
            throw;
        }
    }

    private static Task<Run?> LoadRun(ResearchDbContext db, RunId id, CancellationToken cancellationToken) =>
        db.Set<Run>().AsNoTracking().Include(value => value.Metrics.OrderBy(metric => metric.Sequence))
            .SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    private static bool SameMetric(MetricPoint left, MetricPoint right) => left.Id == right.Id && left.RunId == right.RunId &&
        left.Name == right.Name && left.Value == right.Value && left.Unit == right.Unit && left.Step == right.Step &&
        left.Sequence == right.Sequence && left.RecordedAtUtc == right.RecordedAtUtc;
}
