using ResearchLab.Domain;

namespace ResearchLab.Application;

public enum RunLogStream { Stdout, Stderr }

// Paths are relative to the configured private log root, never arbitrary host paths.
public sealed record RunLogPaths(string Stdout, string Stderr);

public interface IRunLogFiles
{
    RunLogPaths? GetPaths(RunId runId);
    Stream OpenRead(RunId runId, RunLogStream stream);
}

public sealed class RunLogs(IResearchStore store, IRunLogFiles files)
{
    public async Task<RunLogPaths?> GetPathsAsync(RunId runId, CancellationToken cancellationToken = default)
    {
        await RequireRunAsync(runId, cancellationToken);
        return files.GetPaths(runId);
    }

    public async Task<Stream> OpenReadAsync(RunId runId, RunLogStream stream, CancellationToken cancellationToken = default)
    {
        await RequireRunAsync(runId, cancellationToken);
        return files.OpenRead(runId, stream);
    }

    private async Task RequireRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        if (await store.GetRunAsync(runId, cancellationToken) is null)
            throw new CatalogException("RunNotFound", "Run was not found.");
    }
}
