# M3.3 — local Run recovery

M3.3 implements explicit, foreground `RunRecovery.RecoverAsync`. Call it when opening the
local store or when the owner chooses to reconcile abandoned attempts. It is safe to call
while other ResearchLab instances execute. No daemon, scheduler, distributed lock, process
polling, heartbeat, queue or RPC is introduced. Recovery does not launch, resume or kill
any child process and does not infer an outcome from a PID or record age.

```csharp
var store = new SqliteResearchStore(databasePath);
await store.InitializeAsync(cancellationToken);
var result = await new RunRecovery(store).RecoverAsync(cancellationToken);
// result.Recovered: saved Interrupted transitions
// result.Conflicts: still nonterminal after another writer won; retry explicitly later
// result.UnverifiedSessions: active, concurrently claimed, missing, unsafe or unreadable evidence
```

## Minimum owner identity and lifetime

A session is **one caller-owned foreground RunOperation**, not a long-lived host process.
It has a fresh random GUID and a dedicated file at
`<absolute database path>.sessions/<sessionId:N>.session`. Before inserting Created,
SQLite-backed RunExecution creates the file with `CreateNew`, writes a versioned marker
containing the GUID, flushes it to disk and keeps it open with `FileShare.None`.
`OwnerSessionId` is inserted on the same Runs row, in the same INSERT as Created.
There is no window containing a persisted owned Run without its owner ID. No separate
session table or PID/start-time heuristic is required. The immutable technical owner ID
is outside Domain input/source snapshots and is never changed by SaveRunAsync.

The session file remains exclusively open throughout preparation handoff, launch,
Running acknowledgement, execution, cleanup and terminal persistence. Canceling a caller's
wait does not release it. `ProcessCleanupException` retains it across explicit cleanup
retries. Attempt completion or an ordinary error after confirmed cleanup releases it;
if a persistence error left Created/Running, that ended session is recoverable too.
RunOperation.Dispose refuses to release an active or unconfirmed-cleanup attempt. Disposing
an unstarted operation releases the session and permanently prevents its launch; its
remaining Created record can subsequently become Interrupted. A normal completed driver
releases the session automatically. Any insertion/factory error also releases it.

On a host crash the OS releases its open handle. A new process can then acquire exclusive
access to the **existing matching** session file, proving that the original session no
longer owns it under this protocol. Session IDs/files are never reused or reopened for new
execution. Recovery holds its exclusive handle throughout reading and saving, so another
recovery cannot claim the same session simultaneously. Session files are intentionally
retained, not deleted or garbage-collected in M3.3.

## Recovery and concurrency

1. Query distinct non-null OwnerSessionId values for Created/Running; use the new composite
   OwnerSessionId/Status index. Terminal records are excluded before processing.
2. Open each existing file exclusively and validate the exact marker. A sharing conflict,
   absent file, invalid marker, access error or reparse point leaves that session unverified.
   Recovery never creates missing evidence. Neither blocked access nor missing evidence is
   treated as proof of a crash. Unknown legacy owners are left unchanged.
3. While holding the claim, select its pending Run IDs, load each aggregate with its recorded
   metrics, recheck status, capture version V and call Domain.Interrupt(now, reason, V).
4. Save with V through the existing optimistic UPDATE. Exactly one terminal version wins.
   On VersionConflict reload the winner: leave a terminal result unchanged; report a still
   pending Run in Conflicts rather than blindly retrying against changed evidence.
5. Release the claim even on cancellation/storage errors. Successful earlier transitions
   stay committed; a subsequent recovery continues the remainder. No database transaction
   is held over OS/file waits. Ordinary storage/domain errors propagate; a clock that has
   moved behind the last transition is rejected by Domain, never replaced with invented time.

Interrupted's diagnostic contains the session GUID, the missing terminal-outcome reason,
the ownership evidence used and an explicit statement that child termination is unknown.
No private path is stored in this diagnostic. Existing snapshots, metrics, logs, PID and
start time are preserved. ExitCode is unknown. FinishedAtUtc is the **recovery observation
time**, not a claimed process-exit time; Duration for a recovered Running Run includes the
unobserved interval and must not be interpreted as measured execution time.

The minimal Domain change is Created → Interrupted. Created means no startup acknowledgement
was durably saved, not proof that an OS process never existed. The owner can crash before
launch, between launch and saving Running, or while handling a startup-write failure.
Interrupted records lost ownership in all these cases without inventing start metadata,
exit status, success or cancellation. Running → Interrupted and all terminal invariants
remain unchanged. This amendment is reflected in DOMAIN.md and DOMAIN.ru.md.

## Guarantees without a daemon

Recovery is eventually consistent **when explicitly invoked**; nothing wakes up on its own.
It records verified loss of an execution owner, not successful cleanup of that owner's
children. An orphan subprocess can survive a host crash. M3.3 deliberately never signals a
recorded PID: PID reuse and unknown descendant state cannot harm an unrelated process.
A foreign active session remains protected by its exclusive handle even if its Run is old.
No exact process-exit time, lost result, exactly-once execution or automatic retry is claimed.
The startup ambiguity is classified as Interrupted; child identification/termination and
retaining unsaved evidence across a host crash remain separate future work.

This proof requires trusted, local application storage and all instances using the same
absolute database path and sidecar directory. Moving/copying a live database, hard-link
aliases, network filesystems and manual replacement/removal of session files are outside
the contract. Existing path components reject links/reparse points, including the database
and session directory; concurrent filesystem substitution remains a local TOCTOU limitation.
The host must protect this directory from child tasks and other writers. A missing sidecar
is conservatively skipped. File locking is not authentication or an OS process sandbox.

The nullable migration leaves all pre-M3.3/direct AddRunAsync rows unowned; they cannot be
automatically recovered. SQLite RunExecution registers ownership automatically. Alternate
IResearchStore adapters without IRunSessionStore remain compatible but produce unowned,
non-recoverable attempts. Recovery requires the session capability on the same store.
No existing user data is backfilled with guessed ownership.

## Verification

Synthetic tests cover Created/Running recovery, a real second owner process remaining alive
and then being forcibly terminated, unchanged terminal states of all kinds, repeated and
parallel recovery, a competing recovery claim, missing/corrupt evidence, concurrent terminal
and metric updates, preservation of snapshots/evidence, and session retention after canceled
waiting or failed cleanup. Disposing an unstarted operation is verified to forbid later
launch. Tests also exercise idempotent migrations and nullable legacy ownership. Windows is
verified; Unix execution/locking has not been verified in this environment.

M3.6 exposes this use case as `run recover`; see [CLI output and limits](local-run-completion.md).
