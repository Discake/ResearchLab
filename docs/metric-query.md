# M4.4 — Read saved metrics

From the repository root, Windows / PowerShell 7.3+:

```powershell
dotnet build ResearchLab.slnx
pwsh -NoProfile -File ./scripts/metric-smoke.ps1
```

Read the executable, arguments, parameters and fingerprint review on stderr, then type
`yes` to trust this invocation. EOF or another answer declines (exit 5); no Run is created.
The script prepares a new private temporary directory, imports the ProcessFixture demo,
creates an Experiment, starts it with validated `n=2`, prints its saved Run ID, and reads
metrics in separate CLI processes. It checks values, names, repeated steps, sequence order,
and filtered pagination. Files are retained for inspection; `-Directory <new-path>` chooses
a location. This is a calculation demonstration, **not a benchmark**. Values are computed
by the child and ingested from real `@researchlab/v1` stdout events, never inserted by the script.

For any saved Run (use the database and log paths printed in the trust review / retained root):

```powershell
$cli = './src/ResearchLab.Cli/bin/Debug/net10.0/researchlab.dll'
$common = @('--database', '<retained-root>/research.db', '--data-dir', '<retained-root>/logs')
dotnet $cli @common run metrics <run-id>
dotnet $cli @common run metrics <run-id> --name demo_square --offset 2 --page-size 1
```

JSON goes to stdout; diagnostics go to stderr. Existing exit codes apply: 0 success,
2 invalid arguments, 3 unknown Run, 4 incompatible/unavailable storage, 130 cancellation.
A known Run with no points, an unmatched name, or an offset beyond the result returns an
empty `points` array successfully. Metric names match exactly and case-sensitively.
`run show` does not include metric history.

Application exposes `IRunMetricReader.GetMetricsAsync(RunId, name?, RunPageRequest?, cancellationToken)`
and immutable DTOs (`RunMetricPage`, `MetricPointDetails`). SQLite checks Run existence without
loading the aggregate, projects only the requested points with no tracking, and never saves
or migrates on read. The CLI opens SQLite read-only. No EF object or queryable escapes.
No schema migration is needed; existing Run/Sequence and Run/Name/Sequence indexes apply.

Pages default to 50, maximum 100, with nonnegative offset. Filtering precedes pagination.
Points are ordered by the unique ingestion `Sequence` within the stable `runId`; steps
and timestamps may repeat, optional fields remain JSON null. `recordedAtUtc` is the recorded
ingestion time expressed in UTC, not a child-supplied measurement time. Sequence numbers
are not renumbered after filtering. `hasMore` uses one bounded lookahead row; advance
`offset` by the number of returned points, preserving the same name filter. Active Runs
may append evidence between requests; pages are not a frozen multi-request snapshot.

Expected demo evidence is `demo_square=(4,4,16)` at steps `(1,1,2)`, then `demo_input=2`
without step/unit; global sequences `(1,2,3,4)`. IDs and timestamps vary. The full actual
JSON from verification is in [metric-query-actual.json](metric-query-actual.json).
For the filtered command above, the expected and observed page contains just value 16,
step 2, unit `1`, sequence 3, offset 2, pageSize 1, hasMore false.

Verification: `dotnet build ResearchLab.slnx --no-restore`,
`dotnet test --solution ResearchLab.slnx --no-build`, and
`dotnet format ResearchLab.slnx --verify-no-changes --no-restore`.
Tests cover empty/multiple series, repeated steps, global order, Run isolation, missing
Run, bounded pages, filtering, optional fields, reopening via native CLI, and unchanged
database bytes after reads. Automated smoke verification supplies test stdin `yes`; the
shipped script has no automatic approval. Unix execution is unverified. No charts, artifacts,
UI or publication functionality is introduced.

On 2026-10-26: build passed with zero warnings/errors; the full suite passed 414/414,
format verification passed, and the real-process smoke passed. After UTC normalization,
build and the focused MetricQueryTests/ForegroundCliTests passed again (21/21).
An initial sandbox build was canceled; running outside that restriction succeeded.
Piping `yes` directly into the PowerShell script did not forward native stdin and declined
trust correctly; piping it into a new `pwsh -File` process performed the automated smoke.
