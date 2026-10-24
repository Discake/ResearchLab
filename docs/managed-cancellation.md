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
accepted cause wins. Before accepting a late cancel/deadline, the runner checks OS root
exit: an already exited root keeps its natural result. If cancel/deadline was selected
while the root was live, subsequent exit confirms that cause, even if it occurs before
the kill call. Repeated control requests and late timer callbacks cannot select a new cause.
Startup acknowledgement is persisted as Running before any terminal update; controls may
arrive while acknowledgement is pending, but terminal handling waits for it.

- Cancellation before OS startup produces Created → Canceled, without PID or logs.
- Cancellation with confirmed root exit and successful collection produces Canceled.
- Deadline with confirmed root exit and successful collection produces TimedOut.
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

Execution attempts `Kill(entireProcessTree: true)` for a live root and waits at most five
seconds for root exit. After confirmed exit it waits separately at most five seconds for
both drains. A failed/denied kill while the root is live, or an expired wait, raises
`ProcessCleanupException`. Handles, pending I/O, the selected cause and acknowledgement
error remain on the same owner. A drain timeout cancels pending reads/writes and closes
read pipes; a retry checks settled I/O with the same bound. There is no unbounded fallback
wait. These bounds cover asynchronous waits, not OS kill syscall latency, filesystem close
latency or Application database callbacks. No automatic retry loop or worker is created.

The termination evidence is **OS root exit plus settled collection**, with successful
collection required for Canceled/TimedOut. Tree termination is best effort: .NET does not
prove that all descendants have exited; detached children or descendants after an already
exited root are not guaranteed stopped. This contract does not add OS job objects or claim
process isolation. A host must retain ownership and address cleanup failures explicitly;
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
