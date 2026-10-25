# M4.2 — durable Run metric ingestion

`RunMetricIngestion` is an Application owner for one already persisted **Running** Run.
It accepts the typed `MetricEvent` returned by M4.1; it does not parse stdout, launch a
process, or change M3 execution. Call its async methods serially and await each call.

## Contract

- `OpenAsync(store, runId, batchSize, flushInterval, timeProvider)` checks Running
  and captures the persisted version. Batch size defaults to 128, range 1–128.
- `AcceptAsync(event)` records Application arrival time and buffers the event.
  A null result means **buffered, not saved or domain-accepted**. Domain validation
  occurs at flush. At the size threshold or when the interval has elapsed at the
  next arrival, it flushes synchronously. Default interval: one second, maximum one minute.
- `FlushAsync()` validates through `Run.AddMetric` and commits all valid points
  atomically. It returns `SavedCount`, `Version`, and bounded rejections with domain
  code and zero-based index **within that batch**. Consume results from automatic
  flushes too. Rejected events consume neither Sequence nor Version. Incompatible
  units, including null versus a named unit, do not discard valid neighbors.
- `CompleteAsync(ProcessResult)` flushes first, then Application chooses and saves
  Succeeded/Failed/TimedOut/Canceled using existing domain transitions. This is an
  isolated Application API for future workflow integration, not a ProcessRunner hook.
  Persisted evidence survives unsuccessful outcomes and a failed terminal write.
- Storage failures propagate as exceptions. `VersionConflict` is a `CatalogException`;
  SQLite errors retain their original exception. After a flush/completion error the
  owner is stopped, pending memory remains bounded, and no automatic retry occurs.
  Reconcile database state explicitly before creating another owner; never replay a
  computational program automatically. A canceled pre-enqueue call does not add data.
- The owner also stops after successful completion. Created/terminal Runs cannot
  open a write window. External completion racing with a pending batch prevents its
  commit through the version fence. Buffered data has no durability guarantee.

There is no background writer/timer. During idle periods a workflow must explicitly
call `FlushAsync` if it needs an age bound; arrival-driven interval checks alone do
not guarantee an idle-time flush. Always flush/complete at end of collection. A
crash can lose at most the pending batch; only successful commits are durable.

## Persistence and cost

Legacy `SaveRunAsync` loads all N persisted points, compares the immutable prefix,
and attaches the full aggregate. Memory and comparison/read work are O(N + B) for
one save. Repeated small saves can yield O(N²) accumulated history work. It remains
available with its existing semantics for M3; the metric path does not call it.

The minimal `IResearchStore.OpenRunMetricWriteAsync` extension returns a disposable
unit of work exposing the **same Run aggregate**, not another lifecycle model.
Infrastructure loads scalar Run state, MAX(Sequence), and the first saved unit for
each distinct series in the current batch. Application alone calls `Run.AddMetric`
and lifecycle methods, then requests `SaveAsync`. A persistence-only internal Run
window supplies the saved sequence/unit baseline. Its `Metrics` contains only new
points, is capped at 128, and cannot be passed to legacy `SaveRunAsync`.
Normal `GetRunAsync` still returns the complete history.

Memory is O(B), independent of total observations **and total series count**, with
at most B=128 pending observations and B unit baselines. For K distinct batch names,
each flush uses scalar/maximum queries plus K indexed unit lookups and B INSERTs.
The new `(RunId, Name, Sequence)` index supports unit lookups; the existing unique
`(RunId, Sequence)` index supports the maximum and arrival uniqueness. Lookup work
is approximately O((K+1) log N), plus bounded in-batch domain checks O(B²), inserts,
and one Run UPDATE. This is a cost analysis, not measured throughput. No unbounded
history materialization, full series cache, worker, broker or external database.
Migration `MetricSeriesLookup` only adds the lookup index; no evidence is rewritten.

The tracked Run UPDATE uses its loaded Version as the optimistic concurrency token.
EF SaveChanges commits INSERTs and UPDATE together or rolls back all of them. A
concurrent writer can win once; a stale metric or terminal write fails, including
competition with legacy SaveRunAsync. A unique constraint conflict is translated
into VersionConflict only when a changed database Run version confirms the race;
other database errors remain storage failures.

Each valid point increments Run.Version once via AddMetric. A terminal transition
increments it once more. A batch of B valid points moves V to V+B, rather than merely
incrementing once per flush. Sequence independently advances once per valid point.
Old observations are never attached or updated by a write window. A terminal commit
is separate from the final metric commit so evidence remains durable if it fails.

## Verification and integration boundary

`RunMetricIngestionTests` uses actual temporary SQLite files: protocol-to-storage,
reopening, series/repeated step/time ordering, unit rejection, unsuccessful outcomes,
read-only failure, trigger-induced batch rollback and terminal failure, competing
versions, bounded windows and interval/size/explicit flush. Fixtures are synthetic.

Future ProcessRunner integration must deliver parsed events serially with backpressure,
consume every flush diagnostic, drain collection, and transfer sole ownership of the
Run/version to this Application workflow. The existing M3 RunExecution owns a separate
Run instance today and must not keep writing a stale version after ingestion. Fatal
persistence errors must reach that workflow even if a process adapter catches callback
exceptions. Cleanup, cancellation, recovery, success validation and interrupted buffered
batches still require integration work; none is claimed here. Idle flush scheduling and
history paging for readers remain outside this increment.

## Verification — 2026-10-25

Final commands succeeded on this Windows workspace:

```text
dotnet build ResearchLab.slnx --no-restore
dotnet test --solution ResearchLab.slnx --no-build --no-restore
dotnet format ResearchLab.slnx --verify-no-changes --no-restore
dotnet ef migrations has-pending-model-changes --project src/ResearchLab.Infrastructure --no-build
git diff --check
```

Build: zero warnings/errors. Tests: **389 passed, zero failed/skipped**, including
**25 new metric ingestion cases**; final full run took 1m21s. EF reports no pending
model changes. The initial 20 ingestion cases also passed in a focused run:
`dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.RunMetricIngestionTests`.

Earlier attempts: sandbox build/test/format failed because build-host/testing named
pipes were inaccessible, then succeeded outside the sandbox. Initial full suite:
388/389 passed, with the migration-count assertion expecting two instead of three;
that assertion was updated and the complete suite rerun. Format initially found mixed
line endings, now fixed. A premature rebuild while tests were still running failed
on locked DLLs; the final build and test run were sequential and succeeded. Restore
was not rerun, dependency versions were unchanged. No ProcessRunner metric integration
or throughput benchmark was run. No commit or push was made.
