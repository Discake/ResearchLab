# Minimal trusted local execution

This increment implements a synchronous Application workflow through a real Execution
adapter, following M3.1a. It is not the whole M3 milestone.

```csharp
var inspector = new WorkspaceInspector();
var catalog = new ProjectCatalog(store, inspector);
// Import/create Experiment, display ReviewTaskAsync, then explicitly ConfirmTaskAsync
// after the local owner's approval. Keep this same catalog instance.
var execution = new RunExecution(store, catalog, inspector, new ProcessRunner());
var runId = await execution.StartRunAsync(experimentId, parameters, cancellationToken);
var run = await store.GetRunAsync(runId, cancellationToken);
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

The runner uses an explicit executable, literal ArgumentList, UseShellExecute=false and
the validated workspace directory. Windows accepts `.exe` entry points only: `.cmd`/`.bat`
can trigger command-processor fallback even with UseShellExecute=false. M2 import/review
contracts are unchanged; unsupported entry points fail safely at execution. On Unix,
OS execute permissions/interpreter resolution apply; Unix execution is not yet verified.
Trust still covers only the configured entry file/configuration, not transitive libraries,
interpreters or runtime files. This is trusted local code, not an OS sandbox; explicitly
declared interpreter executables remain code the owner must review.

Validated scalar parameters are passed as UTF-8-compatible JSON text through
`RESEARCHLAB_PARAMETERS_JSON`, without command interpolation. Inherited stdin is replaced
with closed redirected input. stdout/stderr are drained concurrently using fixed buffers,
discarded and never stored. A fixed 1 MiB combined byte cap terminates excessive output as
Failed. The configured timeout bounds execution/draining and produces TimedOut after
termination. Cleanup attempts process-tree termination and waits up to five seconds for
the root process; inability to stop is surfaced, never claimed as a successful completion.

CancellationToken is used only up to initial insertion. Once Created is persisted the
workflow owns execution and state writes until completion; canceling the calling request
does not implement CancelRun. No advanced cancellation, background work, log storage,
output/artifact directory allocation, run-history listing or restart reconciliation is
implemented. The inherited environment is not sandboxed. Filesystem TOCTOU races and
the crash window between OS start and saved Running remain as described in the lifecycle
design. Descendant cleanup after the root has already exited is not guaranteed; this
minimal adapter does not add OS job objects or process isolation.

Tests use a dedicated synthetic executable fixture under tests, not a product CLI or
experimental evidence. They cover real SQLite ordering/outcomes, startup failure, missing
trust, parameter errors, changed executable, literal arguments, parameter transport,
both output streams, timeout, output limits and callback-failure cleanup.
