# M2 — persistence and project import

The Application layer owns `ProjectCatalog`, `IResearchStore` and
`IWorkspaceInspector`. Infrastructure implements filesystem/Git inspection and
`SqliteResearchStore`; Domain has no EF, filesystem or process dependency.
No task runner, HTTP host, UI or publication is introduced in M2.

## Using the API

Reference `ResearchLab.Infrastructure` from the future composition root. The following
illustrative C# fragment assumes an existing workspace with the configuration below,
an existing executable and an existing parent directory for the database:

```csharp
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;

var store = new SqliteResearchStore(databasePath);
await store.InitializeAsync(cancellationToken); // Applies migrations; safe to repeat.
var catalog = new ProjectCatalog(store, new WorkspaceInspector());
var imported = await catalog.ImportProjectAsync(workspacePath, "My research", cancellationToken);
var experiment = await catalog.CreateExperimentAsync(imported.Project.Id,
    new ExperimentDefinition("Benchmark", "Compare configurations", "benchmark",
        [new ParameterDefinition("count", ParameterType.Int, required: true, minimum: 1)]),
    cancellationToken);
var projects = await catalog.ListProjectsAsync(cancellationToken);
var experiments = await catalog.ListExperimentsAsync(imported.Project.Id, cancellationToken);
var reopened = await catalog.GetExperimentAsync(experiment.Id, cancellationToken);
```

Creating new store/catalog instances with the same database reopens saved data.
Keep this local database outside imported workspaces to avoid changing their dirty
state; a separate `.researchlab/` directory is ignored in this repository. Paths and
Git references are local metadata, not approved publication data. No default database
is silently created on import: the caller explicitly initializes the selected store.

## Configuration schema v1

An imported directory must contain UTF-8 `researchlab.json`:

```json
{
  "schemaVersion": 1,
  "tasks": {
    "benchmark": {
      "executable": "./build/benchmark",
      "args": [],
      "timeoutSeconds": 120
    }
  }
}
```

On Windows, use the actual relative executable path, e.g. `./build/benchmark.exe`.
All shown fields are required. Unknown/duplicate properties, duplicate task keys,
unsupported schema versions, wrong types and empty task maps are rejected. Arguments
are literal strings, with no shell, environment-variable or command expansion.

Limits: configuration 1 MiB; 1–64 tasks; nonblank keys up to 128 characters; at most
128 arguments; each executable/argument string up to 4096 characters without NUL;
integer timeout 1–86400 seconds. Task executable paths must be workspace-relative
existing files. M2 does not search PATH or permit absolute executable paths. Build
the declared executable before import. Startup/execute permission checks belong to M3.

Workspace paths are canonicalized, including directory links. Config/executable paths
are checked both before and after resolving link chains. Escapes are rejected.
Duplicate imports of the same canonical path are rejected; Windows uses a case-insensitive
workspace key. This is a local single-user boundary, not an OS sandbox against a process
concurrently rewriting the filesystem.

## Explicit trust

Import and experiment creation do not grant execution permission. The caller first
requests `ReviewTaskAsync(projectId, taskKey)`, presents its executable, arguments and
timeout to the local owner, and only after approval calls `ConfirmTaskAsync` with the
reviewed fingerprint. `GetTrustedTaskAsync` rechecks the current fingerprint or fails.
No method in this stage executes that task.

The fingerprint covers project identity, canonical workspace/executable paths, full
configuration bytes and SHA-256 of the selected executable. Changing the configuration
or binary invalidates the old confirmation. Binary review is capped at 256 MiB.
Consent is kept only in the current catalog instance and must be renewed after restart.
It does not attest to scripts, libraries or data transitively loaded by an executable.
The future runner must revalidate immediately before launch and retain the explicit
trust boundary; this review cannot remove filesystem time-of-check/time-of-use races.

## Persistence and invariants

The initial EF migration creates Projects, Experiments, Runs and MetricPoints, foreign
keys, a unique canonical workspace key, and unique `(RunId, Sequence)`. Complex immutable
definitions/snapshots are JSON columns; metrics are individual rows. DateTimeOffset values
retain UTC offsets; the domain produces UTC values. No `EnsureCreated` shortcut is used.

EF materializes aggregates through private constructors/backing fields configured entirely
in Infrastructure. Public domain construction remains validated. Run metrics are still
exposed through a read-only wrapper. Local database files are trusted application state;
manually editing them bypasses the application boundary and is not a supported workflow.

Persist a Run in Created before execution. `SaveRunAsync(run, expectedVersion)` uses the
last persisted version (which may differ from the current in-memory version after several
mutations). It preserves input snapshots, process identity and existing metric rows;
only new metrics and lifecycle metadata are written. EF's version-checked UPDATE and
transaction protect concurrent saves. A conflict rolls back the batch and surfaces
`CatalogException` with `VersionConflict`; there is no silent retry. Terminal Runs cannot
be overwritten. This is application-managed optimistic concurrency for SQLite, as described
in [EF Core documentation](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

## Git inspection

Git is resolved from the host PATH, not the imported working directory. Only fixed read
commands run, with argument lists, no shell, redirected/drained stdout/stderr, a 5-second
limit and 64 Ki characters per stream per command. Git environment overrides, system/global
configuration, fsmonitor and configured clean/smudge/process filters are disabled.
Filter-disabled status can conservatively mark a normally filtered file dirty.

HEAD, origin URL and dirty status are best-effort. HTTP/SSH URL credentials, query and
fragment are removed. Missing Git, non-Git directories, a parent repository instead of a
workspace-root repository, unborn HEAD and exceeded limits produce null fields and diagnostics.
Submodules are not traversed; a gitlink prevents asserting a fully clean working tree.
Source metadata is never a guarantee of exact reproducibility. These commands do not
fetch, clone, contact remotes or run task definitions.

## Verification and next stage

Use the README restore/build/test/format commands. Integration tests cover real SQLite
migrations/reopening, all scalar snapshot types, stale/concurrent writers, immutable data,
invalid configurations, missing/escaping paths, link chains, consent invalidation, and
clean/dirty/non-Git/unborn repositories. Windows was exercised; Unix remains unverified.

Next is M3: real process startup, cancellation/timeout, bounded concurrent stream draining,
Run history and interrupted-run recovery. M2 stores Run data but does not execute runs
or claim to validate process isolation, artifact registration or publication security.
