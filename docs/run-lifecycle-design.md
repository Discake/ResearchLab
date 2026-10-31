# Run lifecycle and executor interaction — proposed design

Implementation update: foreground ownership and cancellation now follow
[managed cancellation](managed-cancellation.md). That contract deliberately returns a
caller-owned attempt and keeps a foreground driver instead of adding the service/async
startup API proposed below. M3.3 recovery now follows [local recovery](run-recovery.md),
which supersedes the ownership/recovery deferrals and the prohibition of Created → Interrupted
in the original proposal below. Broader host/crash supervision remains unimplemented.

Scope: design following M3.1a, not implemented functionality or approval to implement
all of M3. Existing Domain transitions, M0–M2 contracts and `PreparedRun` remain unchanged.
No CLI, HTTP/UI, process control, background scheduler or new dependency is introduced.

## Responsibilities

| Layer | Owns |
| --- | --- |
| Domain | Parameter/default validation, immutable Run snapshots, transition preconditions, timestamps and aggregate version |
| Application | Preparation, consent enforcement, Run creation/persistence, serialized event handling, outcome selection, queries and recovery decisions |
| Infrastructure | Existing SQLite store, configuration/path/content inspection and best-effort source inspection |
| Future Execution adapter | OS startup evidence, bounded stdout/stderr collection, exit evidence and confirmation of requested termination |

The future executor contract belongs to Application and its implementation to Execution.
Define it only when an executor is implemented. It receives typed launch data and reports
facts; it receives neither a mutable Run nor `IResearchStore`. Child stdout cannot assert
process identity, lifecycle state or successful result validation. OS/environment metadata
collection can move behind the existing inspection boundary when additional collection is
actually needed; M3.1a's BCL OS/architecture reads require no new provider abstraction.

## Proposed operations

Names below describe future operations, not added public APIs.

| Operation | Contract |
| --- | --- |
| `PrepareAsync(experimentId, parameters)` | Existing M3.1a: immutable input, no Run record and no execution |
| `StartRunAsync(experimentId, parameters)` | Explicit owner action; fresh preparation, durable Created record, one launch attempt; returns RunId after startup acknowledgement or persisted startup failure |
| `CancelRunAsync(runId)` | Requests cancellation of the owned attempt; request acceptance is distinct from persisted Canceled |
| `GetRunAsync(runId)` | Reads stored state; missing ID produces existing `RunNotFound` semantics |
| `ListRunsAsync(experimentId)` | Future read operation with deterministic ordering and bounded pagination; store support is not present yet |
| Startup reconciliation | Processes runs owned by a previous service session, never active runs of the current session; not a public execution command |

Preparation/lookup/trust/parameter errors fail before Run creation. After Created has been
persisted, known startup failure is recorded on that Run; return its ID with the recorded
Failed outcome rather than losing access to diagnostics. A storage error that prevents
recording an outcome is an operation failure, never a claimed successful transition.
Repeating StartRun is a new attempt with a new RunId; do not retry launches automatically.
An uncertain response must be resolved by reading the known RunId, not silently launching again.

## Start sequence and snapshot boundary

1. Call existing RunPreparation with the same session-local ProjectCatalog used for consent.
   Reuse Domain validation; do not interpolate parameters into executable arguments.
2. Collect available source/environment metadata before Run creation. M3.1a deliberately
   returns unknown source data. A future execution feature can use existing Git inspection
   under that feature's process authorization; never substitute import-time Git state for
   current launch provenance. Unknown fields and diagnostics remain explicit. Collection
   is evidence at observation time, not an atomic source-tree snapshot.
3. Reload Experiment and Project. Structurally compare the definition with the prepared
   definition (including parameter constraints/defaults/allowed values) and check project
   identity/workspace. On change, reject as proposed `PreparationChanged`; prepare again
   through an explicit new attempt. Do not validate against a new definition and silently
   replace the old snapshot. Current storage has no experiment revision token, so comparison
   is structural, not reference equality or Run.Version.
4. Use existing `Run.Create` with that matching Experiment, prepared task/parameters and
   collected metadata, then `AddRunAsync`. Domain revalidation here is intentional. Let
   Domain reject invalid timestamps; do not invent a future creation time to bypass checks.
   The frozen database input starts here. Do not patch snapshots after insertion.
5. Allocate a dedicated per-Run output directory under application-controlled storage via
   a filesystem adapter when implemented. Validate containment/links; do not derive it from
   untrusted task output. Allocation failure after insertion becomes Created → Failed.
6. Under ownership of the attempt, call `GetTrustedTaskAsync` again immediately before
   handoff. Compare its fingerprint, resolved executable path and task values with PreparedRun.
   A newly confirmed but different executable must not replace the prepared executable.
   Missing consent or mismatch prevents launch and records Created → Failed with diagnostics.
7. Hand the future executor a launch description: RunId, validated executable path, working
   directory, literal argument list, copied typed parameters, output directory and timeout.
   Output caps are explicit host policy; do not invent task configuration fields now.
   Parameter/output transport encoding belongs to the later protocol increment.
8. Executor reports either startup failure with no process identity, or confirmed process
   start with positive PID and observation time. Only the latter permits `Run.Start` and
   persistence of Running. Completion must wait behind this persistence acknowledgement.

Trust revalidation remains Application policy using Infrastructure inspection. It does not
turn PreparedRun into a capability that can be replayed. All execution routes must pass
through this policy. Filesystem changes between inspection and OS open remain a documented
TOCTOU limitation of the local single-user model, not an OS sandbox guarantee.

There is no need to add a new Run factory overload for this sequence: matching the reloaded
definition lets the existing factory freeze the intended values. Editing Experiment after
Run insertion is valid and cannot alter the stored snapshots.

## Executor facts and transitions

Use a small typed startup acknowledgement and final outcome rather than a generic event
bus. An attempt has one owner and ordered delivery: start acknowledgement is persisted
before completion is handled. Reports carry RunId and are accepted only from the owning
session. No remote leases, durable queue or protocol for arbitrary child lifecycle events.

| Confirmed fact | Allowed Domain action |
| --- | --- |
| No OS process started; startup failed | Created → Failed via `Fail(reason)`, no PID/start time/exit code |
| Cancellation accepted before any startup attempt | Created → Canceled |
| OS process started with PID/time | Created → Running via `Start` |
| Process exited zero; required collection/validation completed successfully | Running → Succeeded via `Succeed(0, true)` |
| Nonzero exit or essential collection/validation failure | Running → Failed; retain known exit code and reason |
| Explicit cancellation won arbitration and termination/collection completed | Running → Canceled |
| Configured deadline won arbitration and termination/collection completed | Running → TimedOut; Domain verifies elapsed timeout |
| Previous execution owner ended without a recorded terminal outcome | Stale Running → Interrupted with recovery reason |

Exit zero alone never proves Succeeded. The Application completion path determines
`resultsValidated` from actual required collection results, never from child claims.
M3 has no metric protocol yet; do not fabricate metrics or prematurely implement M4.
When evidence ingestion exists, drain, validate and persist accepted evidence before a
terminal transition. Current Domain permits metric append only while Running.

Cancellation requested and deadline reached are control intent, not termination evidence.
Serialize them with start/exit handling for each Run. If cancellation arrives during OS
startup, await the factual startup result: either no process existed, or persist Running
and await confirmed termination. Do not record Canceled while an owned process can still run.
Once normal exit is accepted first, later cancel/timeout intent cannot rewrite that outcome;
if cancel/timeout wins first, later exit only supplies completion evidence for that cause.
Persist exactly one terminal state. Current Domain discards exit code on Cancel/TimeOut;
do not alter that invariant or promise that these fields retain an exit code.

The caller's request CancellationToken controls preparation/waiting, not durable Run
cancellation. After insertion, request disconnection must not discard the attempt's owner
or cancel required status persistence. Explicit CancelRun is a separate operation; service
shutdown and database failure follow ownership/recovery handling, not fabricated cancellation.

## Persistence, duplicates and failure windows

For each mutation: load Run, remember persisted version V, invoke Domain with its current
version, then `SaveRunAsync(run, V)`. If several domain mutations are batched, V remains the
last persisted version. The store's optimistic UPDATE/transaction protects against other
writers; a future in-process per-Run coordinator also serializes control and executor facts.
Do not hold a SQLite transaction open across OS startup or waits.

On VersionConflict reload and inspect the winner. An already persisted identical fact may
be acknowledged without another mutation; conflicting or unverifiable duplicates produce
diagnostics and no rewrite. PID equality alone does not establish attempt identity. Never
blindly replay a domain transition or a process launch. Terminal runs are read-only even
when a later event reports a different outcome. Idempotent acknowledgement, where supported,
belongs to Application; Domain's repeated terminal transitions continue to fail.

| Failure window | Required behavior |
| --- | --- |
| Before Created is durably inserted | Do not launch |
| After Created, with confirmed no startup | Record startup Failed, or Canceled for explicit accepted pre-start cancellation |
| Process exists but saving Running fails | Retain attempt ownership; future executor must stop/collect the owned process and surface persistence failure. Do not record an exit code on Created or claim it never started |
| Stale persisted Running after owner loss | Reconcile to Interrupted, never restart automatically; retain snapshots and recorded evidence |
| Crash between OS start and persistence of Running | Created is ambiguous; do not relaunch or infer that no process existed |
| Terminal outcome observed but save fails | Keep the observed outcome while ownership lives, report storage failure; restart cannot invent a lost success |

The last startup window cannot be closed by current Run fields: PID is saved only with
Running and Created → Interrupted is forbidden. Automatic recovery of ambiguous Created
requires a separately designed durable launch-intent/ownership mechanism and reliable
process identity, or explicit manual resolution with honest diagnostics. This design
does not add such a mechanism, change the state machine, kill a process by a stale PID,
or claim exactly-once execution. Recovery listing/ownership metadata and launch evidence
are unresolved prerequisites for implementing complete M3 recovery safely.

## Verification plan for later implementation

Use a deterministic fake executor for Application tests; actual process tests belong to
the later Execution increment. Verify:

- Created is committed before handoff; preparation failures never launch or insert Run.
- Definition changes are rejected; same-definition reload works; defaults remain Domain-owned.
- Changed fingerprint, removed consent or unsafe paths block handoff even after preparation.
- Startup failure never passes through Running; fast completion waits for saved Running.
- Exit zero with failed collection becomes Failed; diagnostics and valid evidence survive.
- Cancel/start, cancel/exit and timeout/exit orderings produce one justified terminal state.
- Version conflicts and duplicate reports cannot overwrite terminal outcomes or launch twice.
- Request token cancellation after insertion does not abandon persistence/ownership.
- Storage failures and restart windows are reported honestly; ambiguous Created is not relaunched.
- Prepared and persisted snapshots remain immutable; no arbitrary child lifecycle or path input
  can bypass the Application/Infrastructure trust boundary.

This increment adds documentation only. No new tests are needed for it; these cases are
acceptance requirements for future code, not claims that the behavior is implemented.
