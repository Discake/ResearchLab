# Minimal trusted local execution

Managed foreground cancellation is documented in [managed cancellation](managed-cancellation.md).
The execution and cleanup details below include that update.

This increment implements a synchronous Application workflow through a real Execution
adapter, following M3.1a. It is not the whole M3 milestone.

```csharp
var inspector = new WorkspaceInspector();
var catalog = new ProjectCatalog(store, inspector);
// Import/create Experiment, display ReviewTaskAsync, then explicitly ConfirmTaskAsync
// after the local owner's approval. Keep this same catalog instance.
var logFiles = new RunLogFiles(privateLogRoot); // Host-controlled absolute storage root
var execution = new RunExecution(store, catalog, inspector, new ProcessRunner(logFiles));
var runId = await execution.StartRunAsync(experimentId, parameters, cancellationToken);
var run = await store.GetRunAsync(runId, cancellationToken);
var logs = new RunLogs(store, logFiles);
var paths = await logs.GetPathsAsync(runId, cancellationToken);
await using var stdout = await logs.OpenReadAsync(runId, RunLogStream.Stdout, cancellationToken);
```

The caller composes existing Application/Infrastructure components and
`ResearchLab.Execution.ProcessRunner`; no DI container, CLI or host is added.
`IProcessRunner` is owned by Application. Execution does not reference Infrastructure,
access the database or change domain state. The callback confirms a real process start;
Application saves Running before accepting the result. If that callback fails, the
runner stops the owned process and propagates the error, without retrying launch.

`StartRunAsync` waits through completion and terminal persistence, then returns RunId.
This deliberately implements a smaller blocking workflow than the asynchronous start
acknowledgement proposed in the lifecycle design. Preparation/validation failures throw
before insertion. Startup failures return the ID of a persisted Failed Run without PID,
start time or exit code. Nonzero exit produces Failed with the actual exit code; zero exit
and successful bounded stream draining produce Succeeded. There is no metric/artifact
protocol yet, and success makes no claim about scientific validity or expected measurements.

Snapshots use Domain validation/defaults and existing factories. Fresh best-effort source
inspection precedes Run creation; unavailable Git data remains unknown. Reloaded definitions
are structurally compared against preparation. Created is saved before handoff; trust is
rechecked against the prepared fingerprint immediately before execution. Later Experiment
edits cannot rewrite the Run. Persistence uses the last saved version and has no automatic
conflict/launch retries. Database errors propagate instead of claiming a saved outcome.
Ordinary exceptions from the executor after confirmed cleanup produce Failed with a generic
diagnostic and no invented exit code. A failed startup acknowledgement remains the original
application/persistence error even if the adapter wraps or swallows it; no terminal write
is attempted. Unconfirmed cleanup first raises ProcessCleanupException with the retained
owner; after cleanup retry, the original acknowledgement error is propagated.
An I/O/access error in the final trust check fails Created without launching the process.

The runner uses an explicit executable, literal arguments and the validated workspace.
Windows uses [native Job launch and group completion](windows-group-lifecycle.md);
non-Windows retains ArgumentList/UseShellExecute=false. Windows accepts `.exe` entry points only: `.cmd`/`.bat`
can trigger command-processor fallback even with UseShellExecute=false. M2 import/review
contracts are unchanged; unsupported entry points fail safely at execution. On Unix,
OS execute permissions/interpreter resolution apply; Unix execution is not yet verified.
Trust still covers only the configured entry file/configuration, not transitive libraries,
interpreters or runtime files. This is trusted local code, not an OS sandbox; explicitly
declared interpreter executables remain code the owner must review.

Validated scalar parameters are passed as UTF-8-compatible JSON text through
`RESEARCHLAB_PARAMETERS_JSON`, without command interpolation. Inherited stdin is replaced
with closed redirected input. stdout/stderr are drained concurrently using fixed buffers,
saved separately as raw bytes. A fixed 1 MiB combined byte cap terminates excessive output as
Failed. On Windows timeout bounds the entire Job group; root exit alone does not win late
cancel/deadline while descendants remain. Root exit/code, zero Job accounting and successful
collection are required. Cleanup uses TerminateJobObject, with a five-second confirmation
bound and a separate five-second drain bound. Non-Windows retains root/best-effort tree semantics.
Unconfirmed stop or cleanup retains ownership in ProcessCleanupException, without a
terminal write, for explicit cleanup retry. Canceled reads mean failed collection, not EOF.

CancellationToken applies only through initial insertion in StartRunAsync. Once Created is
persisted, the foreground workflow owns execution and mandatory state writes until completion.
For explicit cancellation use PrepareRunAsync and retain its RunOperation: ExecuteAsync drives
the attempt, WaitAsync(token) cancels only waiting, and RequestCancellation requests stopping.
See the managed cancellation contract for races, retries and confirmation requirements.
Run details and paged history reads are implemented through [RunHistory](run-history.md).
Background execution and M4 artifact registration are not implemented. M3.6 adds [per-Run output allocation](local-run-completion.md).
Explicit recovery now follows [M3.3 local recovery](run-recovery.md).

The inherited environment is not sandboxed. Filesystem TOCTOU races and
the crash window between OS start and saved Running remain as described in the lifecycle
design. Windows now contains ordinary CreateProcess descendants in a private Job even after
root exit; the complete production crash-test matrix remains M3.7d. Non-Windows does not
guarantee descendant cleanup after root exit. Job containment is not a filesystem/security sandbox.

Tests use a dedicated synthetic executable fixture under tests, not a product CLI or
experimental evidence. They cover real SQLite ordering/outcomes, startup failure, missing
trust, parameter errors, changed executable, literal arguments, parameter transport,
both output streams, timeout, output limits and callback-failure cleanup.
Regression tests also cover executor exceptions before/after startup, real SQLite write
failures at startup/completion, delayed acknowledgement, final trust-read failure,
incomplete results, and timeout cleanup of a live root with a child inheriting both pipes.

## M3 bounded log retention

The host supplies a private RunLogFiles root outside the imported workspace; neither task
configuration nor stdout selects this path. IProcessRunner receives the persisted RunId
from Application. Handoff creates `<RunId:N>/stdout.log` and `stderr.log` before OS startup.
Trust failures before handoff have no logs. Allocation failure prevents startup and records
Failed with no private paths. Existing attempt directories are rejected and files are never
overwritten. No database migration is needed: paths are derived from RunId.

Retention is capped at 1 MiB combined per Run; stream shares depend on read arrival order.
Each drain uses an 8192-byte buffer, never accumulating all output. Overflow still stops
the process as Failed, retaining bounded stream prefixes. Nonzero exit, timeout and failed
startup acknowledgement do not delete collected bytes. Disk failure leaves partial logs
and cannot report success. Host crashes may lose buffered tail bytes; no fsync/power-loss
durability guarantee is added.

RunLogs checks the Run exists in the store. GetPathsAsync returns relative paths; OpenReadAsync
accepts only RunId and the Stdout/Stderr enum, never arbitrary paths or filenames. Older Runs
without a directory return null; reading a missing file raises a filesystem error. Creation
and reads reject reparse points/symlinks/junctions in files, Run directories, the root and
its ancestors. Concurrent replacement between validation and open remains a TOCTOU limitation
of trusted local storage, not protection against a hostile process with write access to the
root/ancestors. The host must restrict those permissions. M3.6 passes RESEARCHLAB_OUTPUT_DIR
for [per-Run output](local-run-completion.md); this is not M4 Artifact Storage.
