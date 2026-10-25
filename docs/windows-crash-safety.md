# M3.7d — Windows crash and persistence verification

This verifies the existing [Job lifecycle](windows-group-lifecycle.md), without changing
Domain, schema, recovery semantics or the production execution architecture. Before changes,
HEAD `3825ad22ecd00b0aa83978bbe775a5da44af6feb` on `chore/bootstrap` had a clean tree and
passed all 281 existing tests, including M3.7b/M3.7c.

## What the supervisor actually does

Tests start the real CLI entry point in a separate `dotnet` process; they do not replace it
with an in-process ConsoleHost call. Only the synthetic ProcessFixture executable runs as
the declared task. Each scenario has unique events, directories, database records and tokens.
Root/child readiness is acknowledged with named events and a completed JSON manifest.
The supervisor opens real process handles while the acknowledged processes are alive and
waits on those same handles after the crash. PID disappearance is not the exit proof.

The crash action is `TerminateProcess(cli.SafeHandle, 0xE0000037)`, followed by a bounded
CLI-exit wait. It never terminates a tree. Neither supervisor nor fixture parent receives,
opens or duplicates the Run Job handle. In the nested test the supervisor holds only its
own **outer** Job, which remains open with a live parent until all crash assertions and
recovery complete. Thus closing the outer Job cannot hide a broken inner kill-on-close.

Assertions on process exit happen before test teardown. Emergency teardown signals fixture
release events and, if needed, terminates only specifically observed test process handles;
it does not contribute evidence to a passing crash assertion. Event waits are bounded to
20 seconds, fixture waits to 45 seconds, and CLI task timeouts to 60 seconds. No crash moment
is selected solely by an elapsed Task.Delay.

Live roots and children write `finally` marker files only during managed return/unwinding.
Forced owner death leaves those markers absent. When a root was deliberately allowed to
exit earlier, its marker exists, its children are explicitly confirmed alive, and their
markers remain absent after the CLI crash. The instrumented CLI has its own finally marker.

## Checkpoints are absent from production binaries

Building IntegrationTests also builds CLI/dependencies in separate `bin/CrashTests` folders
and copies them to test output `CrashTestCli/`. Only that configuration defines
`RESEARCHLAB_CRASH_TESTS`. Its small `CrashTestCheckpoint` helper sends stage/owner PID/root
PID over a private pipe and waits for the supervisor's `continue` or process termination.
Hooks are compile-time guarded; Debug/Release have no helper type or environment-controlled
pause. A production-CLI test checks that absence and supplies the same variables without
causing a pause. No packages or production checkpoint framework were added.

The native-created hook is after successful CreateProcess and immediate SafeHandle adoption,
before launcher return/managed acknowledgement. Its test observes a live root handle directly
and declares no descendants. It does not wait for later managed fixture readiness while the
launcher is deliberately paused with temporary stdio ends still open. Later startup hooks
test a fully ready root and two descendants. Native setup remains unchanged in shipping code.

## Critical scenario matrix

All abrupt crashes below terminate **only CLI**. `H` means every observed root/child process
handle becomes signaled within the bounded wait; `I` means explicit CLI recovery produces
Interrupted, preserving all four serialized input snapshots, CreatedAt, saved PID/StartedAt,
and null exit code. Repeated recovery preserves terminal version/time/diagnostic.

| Scenario / precondition | Synchronization / crash point | Observed result | Remaining scope |
| --- | --- | --- | --- |
| Long-lived root, Running | RootReady + durable Running, then CLI termination | H; no root finally; Running remains until I | Fixture executable only |
| Root + one child, shared stdout/stderr | Both ready; child log records observed | H for both; no managed finally; I | Ordinary CreateProcess descendant |
| Root + two children, shared pipes | All three ready; child records observed | H for all; I | Two controlled descendants |
| Root + two children, separate stdio | Children have their own redirected pipes; their records absent from Run stdout/stderr | H for all; I | Separate pipe handles, not a filesystem sandbox |
| Root exits first; two children retain pipes | RootExit event; root handle signaled; children confirmed live; then CLI termination | Children stop without their finally; root's earlier finally is present; I | Root exit alone is not terminal evidence |
| Root exits first; separate child stdio | Same barriers, without inheriting Run stdout/stderr | Children stop; I | Does not depend on inherited log pipes |
| Before Job creation; Created | Pipe checkpoint before CreateJobObject | No declared program creation; no manifest; Created → I | Exact managed checkpoint |
| Configured Job; Created | Pipe checkpoint after limit setup, before CreateProcess | No declared program creation; Created → I | Job exists without a root |
| Successful native creation; Created | Pipe packet contains actual live root PID; supervisor opens its handle | Root H; no finally; saved PID remains unknown; I | API returned; no claim about its internal instructions |
| Before saving Running; Created | Pipe checkpoint, then ready root + two children | H; no fabricated saved PID/StartedAt; I | Before the database transition |
| After saving Running | Pipe checkpoint after SaveRunAsync; ready group | H; Running → I; saved startup evidence preserved | After the database transition |
| Group completed, terminal result not saved | Release fixtures; before-terminal checkpoint; all H naturally | Durable Running → I, never reconstructed Succeeded | Unsaved successful result is lost |
| Succeeded already durable | After-terminal checkpoint; all fixtures already exited | Succeeded stays Succeeded; recovery does not rewrite it | This is legitimate pre-crash success |
| Two CLI owners, same DB | Both Running; both have observed log writes; kill first CLI | First H + I; second group remains live/Running and later saves Succeeded | Distinct projects/workspaces and private outputs in one DB |
| Corrupt session evidence after a real crash | Group H; corrupt only the test sidecar | Session reported unverified; Running/version unchanged; restore evidence → I | Unavailable evidence is not treated as dead ownership |
| SQLite rejects Running | At before-running, install test trigger, continue normally | CLI exit 4; whole group stops; durable Created → I | Storage-failure control, not an abrupt crash |
| SQLite rejects terminal success | Before-terminal trigger, continue normally | CLI exit 4; durable Running → I; no fake Failed/Succeeded | Execution result was not committed |
| Saved PID names an unrelated live fixture | Synthetic ended session deliberately records sentinel PID; recover | Sentinel survives both recoveries; owned records become Interrupted | Controlled stale/reused-PID stand-in, not random PID reuse |
| CLI from ordinary fixture parent | Parent stays alive while CLI is killed | H + I while parent survives | Ambient membership is measured, not assumed absent |
| CLI attached to a console | Fixture parent allocates a hidden Win32 console; child CLI inherits it | H + I; console parent survives | Real console attachment; not a separate Windows Terminal/IDE test |
| Compatible nested Jobs | Parent and CLI in supervisor-owned outer Job; task in private inner Job | Task membership in outer Job verified; H + I while outer Job/parent remain alive | No UI restrictions or arbitrary outer limits tested |

For log isolation, the fixture emits bounded synthetic text past the FileStream buffer and
the supervisor observes actual stored records before crashing. It verifies token separation
and distinct output directories. This does **not** promise that every buffered tail byte
survives owner death; crash-induced loss of unflushed log tails remains possible.

## Platform observations and limits

The tested machine reports Windows x64, build **26200.9711**, display version **25H2**;
the test runtime reports .NET **10.0.12**, SDK **10.0.401**. These are observations of this
machine, not a claim about every Windows 10/11 or Server build. Named-event/Job tests skip
explicitly outside Windows 10+. No Unix or other-architecture verification is claimed.

Each parent-context case saves a small synthetic evidence JSON under
`tests/ResearchLab.IntegrationTests/bin/<configuration>/net10.0/TestResults/windows-crash-context-*.json`:
OS/runtime, ambient Job membership and the fact that CLI-only termination/recovery passed.
The compatible nested case separately asserts task membership in the controlled outer Job.
On this run, the saved JSON reports `CliInAmbientJob=true` for ordinary, console-attached
and explicitly nested contexts. Thus ordinary/console success must not be advertised as
testing a Job-free ancestor; the runner environment already provided an ambient Job.
An allocated console exercises console attachment; interactive Windows Terminal, VS Code
terminal, Explorer launch and every possible terminal-host Job policy were not directly tested.

Deterministic pauses surround meaningful managed/API-return/persistence boundaries. They
cannot pause every instruction inside CreateProcessW or a SQLite/OS syscall. This suite
does not relabel repeated checkpoint runs as a proof of those unobservable micro-windows;
no exhaustive in-syscall stress guarantee is made. Power failure, OS crash, hostile same-user
handle duplication, broker-created processes outside ordinary Job inheritance and arbitrary
ancestor Job limits remain outside the verified matrix. Kill-on-close is not a security sandbox.

Interrupted still means **verified owner loss**, not proof reconstructed from the database
that all children stopped, not a lost success, and not a recovered computation. The live
supervisor's process-handle observations are separate evidence from recovery's session proof.
Domain and database schema were not changed. No execution defect requiring an architecture
change was found; intermediate failures were in test synchronization/teardown/log observation.

## Reproduce

```powershell
dotnet build ResearchLab.slnx --no-restore
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.WindowsCrashSafetyTests
dotnet test --solution ResearchLab.slnx --no-build
dotnet format ResearchLab.slnx --no-restore --verify-no-changes
git diff --check
```

The standalone crash matrix passed 21/21 before the final strengthened full regression.
Final verification on 2026-10-25: **302/302 full-solution tests passed, zero skips**,
including all 21 strengthened crash cases and all prior scenarios. Debug and instrumented
CLI builds passed with zero warnings/errors; formatting and diff whitespace checks passed.
The local runner does not support the attempted `--report-trx` switch; that attempt ran zero
tests. Its `dotnet test ... -- --help` path also failed in the installed SDK. The commands
above work without either option; no reporting dependency was introduced to work around it.

Official references: [kill-on-last-close](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-createjobobjectw),
[TerminateProcess and asynchronous exit](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess),
[nested Job inheritance/limits](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs),
[console creation/attachment](https://learn.microsoft.com/en-us/windows/console/creation-of-a-console).


M3.7e consolidates the final checks, OS scope, crash guarantees and remaining limits:
[final verification (2026-10-25)](m3-final-verification.md). Earlier counts on this page
are historical milestone results, not the latest suite total.
