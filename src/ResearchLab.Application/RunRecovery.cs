using ResearchLab.Domain;

namespace ResearchLab.Application;

// Technical ownership, separate from frozen research input and OS child identity.
public interface IRunSession : IDisposable
{
    Guid Id { get; }
    Task AddRunAsync(Run run, CancellationToken cancellationToken = default);
}

public interface IRunSessionStore
{
    IRunSession OpenRunSession();
    Task<IReadOnlyList<Guid>> ListIncompleteSessionsAsync(CancellationToken cancellationToken = default);
    // Null means active or unverified. Never create missing evidence or release a live owner.
    IDisposable? TryClaimEndedSession(Guid sessionId);
    Task<IReadOnlyList<RunId>> ListIncompleteSessionRunsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record RunRecoveryResult(IReadOnlyList<RunId> Recovered, IReadOnlyList<RunId> Conflicts,
    IReadOnlyList<Guid> UnverifiedSessions);

public sealed class RunRecovery(IResearchStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly IRunSessionStore sessions = store as IRunSessionStore
        ?? throw new ArgumentException("The store must support durable execution sessions.", nameof(store));

    public async Task<RunRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var recovered = new List<RunId>();
        var conflicts = new List<RunId>();
        var unverified = new List<Guid>();
        foreach (var sessionId in await sessions.ListIncompleteSessionsAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var claim = sessions.TryClaimEndedSession(sessionId);
            if (claim is null)
            {
                unverified.Add(sessionId);
                continue;
            }
            foreach (var id in await sessions.ListIncompleteSessionRunsAsync(sessionId, cancellationToken))
            {
                var run = await store.GetRunAsync(id, cancellationToken);
                if (run is null || run.Status is not (RunStatus.Created or RunStatus.Running))
                    continue;
                var version = run.Version;
                run.Interrupt(clock.GetUtcNow(),
                    $"Execution session {sessionId:N} ended without recording a terminal outcome. " +
                    "Recovered from durable ownership and exclusive session-file access; child process termination is unknown.", version);
                try
                {
                    await store.SaveRunAsync(run, version, cancellationToken);
                    recovered.Add(id);
                }
                catch (CatalogException exception) when (exception.Code == "VersionConflict")
                {
                    // Inspect the winner, but never replay a stale transition or overwrite its evidence.
                    var winner = await store.GetRunAsync(id, cancellationToken);
                    if (winner?.Status is RunStatus.Created or RunStatus.Running)
                        conflicts.Add(id);
                }
            }
        }
        return new(recovered.AsReadOnly(), conflicts.AsReadOnly(), unverified.AsReadOnly());
    }
}
