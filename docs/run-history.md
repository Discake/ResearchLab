# M3 Run history reads

`RunHistory` uses the existing `IResearchStore` and SQLite adapter. It does not execute
tasks, reconcile stale Runs, update status/version or save aggregates.

```csharp
var history = new RunHistory(store);
var details = await history.GetRunAsync(runId, cancellationToken);
var page = await history.ListRunsAsync(experimentId,
    new RunPageRequest(offset: 0, pageSize: 25), cancellationToken);
// Reuse the existing log service with the same configured private log root:
var logs = new RunLogs(store, logFiles);
var paths = await logs.GetPathsAsync(details.Id, cancellationToken);
// paths is null when no log directory exists; paths are relative to the log root.
```

Details and list items contain ID, Experiment ID, status, creation/start/finish dates,
copied read-only typed parameters, exit code and a safe diagnostic. They expose no mutable
Run, workspace/executable paths, environment snapshot or arbitrary stored FailureReason.
Diagnostics are generated from status/start evidence/exit code, not filtered exception
text; raw persisted diagnostics remain unchanged. Parameters remain the recorded user
inputs and are not a secret-redaction mechanism. Logs may contain private process output;
read them locally through the existing `RunLogs.OpenReadAsync(RunId, RunLogStream, token)`.
No new log path input or filesystem reader is added.

Unknown Run IDs throw `CatalogException` with code `RunNotFound`; unknown Experiment IDs
throw `ExperimentNotFound`. An existing Experiment with no Runs returns an empty list.
The store's existing nullable GetRunAsync contract is unchanged.

Pages default to 50 items, accept 1–100 items and a nonnegative integer offset. Invalid
values throw `ArgumentOutOfRangeException` before accessing SQLite. Results are ordered
by CreatedAtUtc descending, then ID ascending in SQLite TEXT order. Domain normalizes
Run timestamps to UTC; the existing SQLite TEXT format preserves chronological ordering
including fractional seconds. Parameterized SQL applies experiment filtering, ordering,
LIMIT and OFFSET before materialization. No full-history load, metric join or total-count
query is performed. No schema migration or general query framework is needed.

An empty/short page marks the current end. Separate requests are not a shared snapshot:
concurrent inserts may shift offsets and cause repeats/skips. No stable cross-request
snapshot or cursor guarantee is claimed. Cancellation tokens reach the SQLite reads.

Integration tests use synthetic SQLite data, cover empty/unknown history, experiment
isolation, tied timestamps/ID order, subsecond precision, repeated pages, limits/defaults,
all statuses, preserved versions/evidence, immutable parameter copies and existing logs.
