using System.Text;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Persistence;

public sealed partial class SqliteResearchStore
{
    public IRunSession OpenRunSession()
    {
        try
        {
            CheckSessionPath(databaseFile);
            CheckSessionPath(sessionRoot);
            Directory.CreateDirectory(sessionRoot);
            CheckSessionPath(sessionRoot);
            var id = Guid.NewGuid();
            var stream = new FileStream(SessionPath(id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try
            {
                stream.Write(SessionMarker(id));
                stream.Flush(flushToDisk: true);
                return new RunSession(this, id, stream);
            }
            catch { stream.Dispose(); throw; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CatalogException("SessionStorageUnavailable", "Exclusive local execution-session storage is unavailable.");
        }
    }

    public async Task<IReadOnlyList<Guid>> ListIncompleteSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await db.Set<Run>().Where(run => (run.Status == RunStatus.Created || run.Status == RunStatus.Running) &&
                EF.Property<Guid?>(run, "OwnerSessionId") != null)
            .Select(run => EF.Property<Guid?>(run, "OwnerSessionId")!.Value).Distinct().OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    public IDisposable? TryClaimEndedSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return null;
        FileStream? stream = null;
        try
        {
            CheckSessionPath(databaseFile);
            var path = SessionPath(sessionId);
            CheckSessionPath(path);
            // Open, never OpenOrCreate: absent or corrupt evidence is not proof of owner loss.
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var marker = SessionMarker(sessionId);
            var actual = new byte[marker.Length];
            if (stream.Length != marker.Length || stream.Read(actual) != marker.Length || !actual.SequenceEqual(marker))
            {
                stream.Dispose();
                return null;
            }
            return stream; // Hold exclusivity throughout recovery, including version-checked writes.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null; // Active, inaccessible, missing or unsafe: all conservatively unverified.
        }
    }

    public async Task<IReadOnlyList<RunId>> ListIncompleteSessionRunsAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = new ResearchDbContext(options);
        return await db.Set<Run>().Where(run => EF.Property<Guid?>(run, "OwnerSessionId") == sessionId &&
                (run.Status == RunStatus.Created || run.Status == RunStatus.Running))
            .OrderBy(run => run.Id).Select(run => run.Id).ToListAsync(cancellationToken);
    }

    private string SessionPath(Guid id) => Path.Combine(sessionRoot, id.ToString("N") + ".session");
    private static byte[] SessionMarker(Guid id) => Encoding.ASCII.GetBytes($"researchlab-session/v1\n{id:N}\n");

    private static void CheckSessionPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Links are not allowed in execution-session storage.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class RunSession(SqliteResearchStore store, Guid id, FileStream stream) : IRunSession
    {
        private readonly object gate = new();
        private bool used;
        private bool adding;
        private bool disposed;
        public Guid Id { get; } = id;

        public async Task AddRunAsync(Run run, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (used) throw new InvalidOperationException("A foreground session owns exactly one Run.");
                used = adding = true;
            }
            try { await store.AddRunCoreAsync(run, Id, cancellationToken); }
            finally { lock (gate) adding = false; }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (adding) throw new InvalidOperationException("Finish registering the Run before ending its session.");
                disposed = true;
                stream.Dispose();
            }
        }
    }
}
