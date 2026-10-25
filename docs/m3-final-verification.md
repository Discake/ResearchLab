# M3.7e — final verification (2026-10-25)

M3.7b/c/d were present on `chore/bootstrap` with a clean working tree before this
documentation pass. No production code, Domain contract, migration or dependency changed.

## Executed checks

| Command | Observed result |
| --- | --- |
| `dotnet restore ResearchLab.slnx` | Passed |
| `dotnet build ResearchLab.slnx --no-restore` | Passed; zero warnings/errors; includes separate CrashTests binaries |
| `dotnet test --solution ResearchLab.slnx --no-build --no-restore` | 302/302 passed, zero skips; repeated outside sandbox with the same result |
| `dotnet format ResearchLab.slnx --verify-no-changes --no-restore` | Passed initially and after documentation edits outside sandbox; intervening sandbox repeat failed loading MSBuildWorkspace |
| `dotnet ef migrations has-pending-model-changes --project src/ResearchLab.Infrastructure` | Passed; no pending changes |
| `git diff --check` | Passed after documentation edits |

The full suite includes the 21 [Windows crash cases](windows-crash-safety.md), import,
parameter validation, trust/revalidation, independent/concurrent Run storage, stdout/stderr,
history, SQLite, cancellation/recovery and Domain terminal/snapshot invariants. These are
synthetic fixtures, not research results. `dotnet --info` partially reported installed
versions but exited 1 in sandbox because Service Control Manager access was denied.

## Foreground demonstration

The real preparation script initialized fresh storage, imported the demo and created its
Experiment. Outside sandbox, explicit `yes` on stdin approved each reviewed invocation:
success saved Succeeded/0 with `Sum: 20`; `fail=true` saved Failed/7 (CLI exit 6).
Reopening logs/history worked; distinct Run directories each contained `output`.
Changing only the disposable demo configuration to a one-second timeout and requesting a
30-second delay saved TimedOut/null exit code (CLI exit 124). Explicit `run recover`
completed with empty lists for these already terminal Runs.

Ctrl+C was verified by the passing real console-control-event test, including
Program/CancelKeyPress and durable Canceled, rather than a human keyboard press in a
visible terminal. Recovery after owner death was verified by the crash matrix, rather
than a separate manual crash. No claim of a completely manual nine-step walkthrough is made.
Output allocation was observed; the sum demo itself does not write an artifact.

The first demo used sandbox temporary storage and saved pre-start Failed with null PID/exit
code. Repeating with ordinary temporary storage outside sandbox passed. This observation
does not establish which sandbox restriction caused launch failure and is not evidence of
a production crash defect. Critical crash tests passed in both suite runs.

Preparation and a success/log round trip also passed in PowerShell 7.6.5 and Windows
PowerShell 5.1.26100.9711 with explicit UTF-8 console encodings. In 5.1, JSON quotes needed
legacy escaping; the verified value used `shell-check` without spaces. Two attempted 5.1
variants with `shell check` failed CLI parsing. Prefer PowerShell 7.3+ Standard argument
passing for arbitrary JSON; do not advertise unrestricted 5.1 argument compatibility.
An initial non-ASCII demo label displayed replacement characters in captured logs with
default console encodings; setting shell encoding cannot repair bytes already emitted by
a child. Logs retain raw bytes and readers decode as UTF-8 with replacement.

## Platform and guarantee boundary

Verified here: Windows 11 25H2 x64, build 26200.9711, SDK 10.0.401, runtime 10.0.12.
Windows 10+ x64 with the required creation-time Job attributes and compatible ancestor
Jobs is the intended compatibility boundary; other Windows 10/11 builds, Windows Server,
ARM64/x86, Windows Terminal/IDE launch policies and restrictive ancestor Jobs were not
verified. Linux/macOS execution and locking were not run.

Windows creates a private noninheritable kill-on-close Job and supplies it to CreateProcessW
at creation, with an explicit standard-handle list. Job creation/configuration/attribute or
process creation failure fails startup; no uncontained fallback is allowed because it would
discard the ownership guarantee. An incompatible ambient Job can therefore reject a task.

Run completion waits for root exit/code, zero active Job processes and settled collection:
descendants can outlive the root and close or retain pipes independently. Root exit zero
does not prove every descendant succeeded; their exit codes are not collected. Succeeded
describes this execution/collection contract, not scientific correctness.

Abrupt CLI death closes its private Job handle and the OS terminates ordinary contained
descendants when the last Job handle closes. The crash matrix observes actual process
handles; it does not prove every instruction inside OS/database calls, power-loss durability
or preservation of buffered log tails. An unsaved terminal outcome remains Created/Running
until explicit recovery. Interrupted records verified owner loss, never a recovered result
or database proof that descendants stopped. Recovery never signals stored PIDs.

Linux has no equivalent Job boundary in this implementation: root exit and best-effort
`.NET Kill(entireProcessTree: true)` do not prove cleanup of descendants after root exit or
CLI death. Neither backend is a security sandbox. Jobs do not restrict filesystem/network
access, credentials or external services. Broker/service-created work outside ordinary Job
inheritance and hostile handle duplication are outside the guarantee. Killing local
processes does not roll back external requests or stop remote computations.

Private local storage and cooperative ownership remain assumptions. Link/reparse checks
do not eliminate TOCTOU, same-user hostile writes, storage aliases or sidecar replacement.
Output allocation is not artifact validation, quotas or immutable evidence. See
[ProcessRunner](minimal-execution.md), [cancellation](managed-cancellation.md),
[recovery](run-recovery.md) and [M3.6](local-run-completion.md).

## Completion and next increment

The M3 local execution criteria are closed within this Windows scope: trusted foreground
launch, distinct attempts, recorded inputs/status/history, bounded separate logs, failure
diagnostics, cancellation/timeout, explicit ownership recovery and verified Job crash
containment. No critical crash-test defect was observed. A separate defect for a large
execution rewrite is not justified by these results.

M4 can begin. The whole MVP remains incomplete: protocol ingestion, registered immutable
artifacts, metric comparisons, UI and Research Story export are later stages. Visible
terminal Ctrl+C walkthroughs, additional OS versions and general 5.1 JSON quoting remain
verification gaps, not silently completed acceptance evidence.
