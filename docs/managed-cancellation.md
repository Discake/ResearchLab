# Managed cancellation of foreground local Runs

This is the implemented Application/Execution contract extending
[the lifecycle design](run-lifecycle-design.md). Domain transitions and database schema
are unchanged. There is no daemon, queue, detached driver, RPC or cancellation from another
process. The caller holds one `RunOperation` for each attempt. The [foreground CLI](foreground-run.md)
now drives this contract, including Ctrl+C. Its foreground cleanup retries keep the same
owner; the Execution adapter itself still does not run an automatic retry loop.

## Ownership and waiting

```csharp
var operation = await execution.PrepareRunAsync(experimentId, parameters, preparationToken);
var driver = operation.ExecuteAsync(); // Keep and eventually await this foreground task.
try
{
    await operation.WaitAsync(requestToken);
}
catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
{
    // Only this wait ended. The operation and driver still own the process.
}
// Explicit owner action, also suitable for a future foreground Ctrl+C handler:
operation.RequestCancellation();
try
{
    await driver;
}
catch (ProcessCleanupException error)
{
    // Retain error.Operation (the same operation); report unconfirmed cleanup.
    // When the owner chooses to retry cleanup, await operation.ExecuteAsync().
    // Repeated failure retains ownership again; it never launches another process.
    throw;
}
```

`PrepareRunAsync` validates and persists Created, freezes snapshots, and returns an owner
without starting an OS process. The preparation token applies through insertion only.
The owner must drive or explicitly cancel and drive the returned attempt; simply dropping
it leaves Created, not a canceled Run. Source information describes preparation-time
observation, not an atomic or continuously refreshed working-tree snapshot.
`ExecuteAsync` rechecks task trust/fingerprint immediately before the first launch. It
accepts no request token. `WaitAsync(token)` requires an already started driver and cancels
only that wait. `RequestCancellation()` is thread-safe and only records intent; it returns
true if cancellation is the selected cause, including repeated requests. A false result
means another cause won. Neither value is a saved terminal status.

Repeated/concurrent `ExecuteAsync` calls share the same driver task. Only an observed
`ProcessCleanupException` allows another driver invocation, resuming cleanup on the same
execution handle. `StartRunAsync` remains a convenience that awaits the whole workflow;
its token does not stop execution after insertion. If its cleanup fails, the exception's
`Operation` exposes the retained Application owner. Direct Execution callers retain
`ProcessCleanupException.Execution` instead. Ordinary execution exceptions are permitted
only after confirmed cleanup; Application maps them to a sanitized Failed diagnostic.

## Arbitration and terminal evidence

A per-attempt lock serializes startup, cancellation and deadline selection. The first
accepted cause wins. Before accepting a late cancel/deadline, the runner checks completion:
on Windows this requires root exit/code and an empty owned Job, so a dead root with live
descendants remains cancelable. Non-Windows checks root exit. If control was accepted before
completion, later exit does not change its cause. Repeated requests and late timer callbacks
cannot select another cause. See [Windows group evidence](windows-group-lifecycle.md).
Startup acknowledgement is persisted as Running before any terminal update; controls may
arrive while acknowledgement is pending, but terminal handling waits for it.

- Cancellation before OS startup produces Created → Canceled, without PID or logs.
- Cancellation with confirmed execution completion and successful collection produces Canceled.
- Deadline with confirmed execution completion and successful collection produces TimedOut.
- Natural exit zero and completed collection produce Succeeded; nonzero exit produces Failed.
- Essential collection failure produces Failed even if cancel/deadline initiated shutdown.
- Unconfirmed stop/cleanup produces an exception and **no terminal write**. Running remains
  Running (or Created remains Created if saving Running failed).

There is one Application completion path and one terminal save using the last persisted
version; the store still enforces optimistic concurrency. Domain keeps terminal states
final and retains no exit code for Canceled/TimedOut. An acknowledgement persistence error
stops the owned process before propagating the original error. If stopping also fails,
cleanup failure is surfaced first with its retained owner; after successful cleanup retry,
the original persistence error is propagated, with no fabricated terminal update. Terminal
save errors propagate and are not automatically replayed or reclassified.

## Cleanup limits and limitations

On Windows Execution attempts `TerminateJobObject` and confirms root exit plus zero Job
accounting, releasing its root handle after saving the exit code. Non-Windows retains
`Kill(entireProcessTree: true)` and root-exit confirmation. Forced-cleanup confirmation and
then both drains are bounded separately to five seconds. A denied stop without completion,
unavailable group evidence, or an expired wait raises
`ProcessCleanupException`. Handles, pending I/O, the selected cause and acknowledgement
error remain on the same owner. A drain timeout cancels pending reads/writes and closes
read pipes; a retry checks settled I/O with the same bound. There is no unbounded fallback
wait. These bounds cover asynchronous waits, not OS kill syscall latency, filesystem close
latency or Application database callbacks. No automatic retry loop or worker is created.

Windows evidence is **OS root exit/code, zero active Job processes and settled collection**,
with successful collection required for Canceled/TimedOut. Actual Job loss cannot be repaired
by trusting EOF or stored PIDs. Non-Windows still offers only root exit and settled collection;
its best-effort .NET tree kill does not prove descendant termination after root exit.
Neither backend is a security sandbox. A host must retain ownership and address cleanup failures explicitly;
request cancellation never disposes the owner or cancels mandatory status writes.
M3.3 [local recovery](run-recovery.md) now records verified ownership loss as Interrupted,
including ambiguous Created startup. It does not confirm or terminate orphan children.

Tests use real synthetic fixture processes and SQLite. They cover canceled waiting with a
live retained owner, long-process cancellation, late cancel after natural exit, both orders
and a concurrent race of cancel/deadline, repeated cancel/driver calls, denied stop followed
by cleanup retry, failed acknowledgement combined with denied stop, pre-start cancellation,
launch-time trust revalidation and one terminal version. A manually triggered TimeProvider
makes deadline races deterministic; it does not fabricate process-exit evidence. Real-time
timeout/tree cleanup, output limits, persistence failures and stream collection also remain
covered by the execution tests. Windows is verified; Unix behavior is not verified here.


M3.7e consolidates the final checks, OS scope, crash guarantees and remaining limits:
[final verification (2026-10-25)](m3-final-verification.md). Earlier counts on this page
are historical milestone results, not the latest suite total.
