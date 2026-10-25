# M3.7c — Windows Job group execution lifecycle

On Windows, the production `ProcessRunner` (and therefore the foreground CLI) now uses
the [native launcher](windows-native-launcher.md). A missing/unsupported Job containment
configuration fails startup without falling back to `Process.Start`. Windows 10+ is required;
ambient Job restrictions can still reject startup. Domain transitions, Application lifecycle,
RunRecovery and SQLite schema are unchanged. The `IProcessExecution` comment now describes
the owned execution boundary rather than promising only root termination.

## Responsibilities and completion evidence

`OwnedProcess` retains the shared launch/storage preparation, arbitration, timer, log drains,
1 MiB combined cap, acknowledgement handling and cleanup retry. `IRunProcess` is a small
internal resource adapter. `WindowsRunProcess` owns native process/Job resources and caches
root exit evidence; `WindowsJobControl` wraps the two Job APIs. `LocalRunProcess` keeps the
non-Windows backend's existing root-exit/best-effort tree-kill behavior. It does not claim
equivalent group guarantees and has not been verified on Unix here.

Windows natural completion requires a signaled root process handle, its saved exit code,
`QueryInformationJobObject(JobObjectBasicAccountingInformation).ActiveProcesses == 0`,
and settled stdout/stderr drains. Succeeded additionally requires root exit zero and no
collection or group-observation errors. Descendant exit codes are not collected or claimed.
EOF, root exit, successful TerminateJobObject and closing the Job are not substitutes for
the zero-count proof. Job-handle signaling and PID-list emptiness are not used as a barrier.

Arbitration is serialized under the same per-attempt gate. An observed completed group
wins late cancellation/deadline; a dead root with live descendants does not. Timeout remains
armed until the group has completed. Cancel/deadline/critical output errors request
`TerminateJobObject`, including after root exit. The first selected control cause remains;
essential collection or group-query failure prevents a successful/canceled/timed-out result
and produces Failed only after cleanup can be confirmed. Startup acknowledgement still
settles before a result or ordinary exception is returned.

## Native reference release order

The launcher allocates the resource owner before CreateProcess and closes the primary
thread handle immediately after adopting successful startup. Child pipe ends, attribute
values/list, environment block and stdin writer are released when launch returns.
The Windows adapter is also allocated before launch, avoiding fallible wrapping after startup.

Under the runner gate, the adapter polls the root handle without a background OS wait
registration. Once signaled, it reads the exit code and **closes its root process handle**.
Only then does it poll Job accounting. No managed `Process` wrapper, duplicate process
handle, thread handle or root wait registration is kept by the runner while awaiting zero.
The Job and byte-stream handles remain owned; they are not root-process references.

Microsoft documents that accounting can decrement only after a terminated process exits
and references are released. Other owners' process/thread references or OS teardown can
therefore conservatively delay the accounting proof. Tests include a real accounting probe
with a held root reference and after releasing it, plus normal/group runs that must complete
without deadlocking on the runner's own references. The observed pre-release count is
diagnostic, not a portable assertion that every Windows version behaves identically.

After zero is confirmed, both drains must settle; then logs are disposed, the Job is closed
and remaining pipe/resources released. Closing an already empty Job is resource release,
not the completion proof. No log file is released while its drain is pending.

## Failure and retry contract

A query error is sticky diagnostic evidence against Succeeded. The driver tries group
termination and confirmation; it cannot convert an unknown count to zero. A denied/failed
TerminateJobObject may still be followed by independent completion evidence, but its success
alone never confirms anything. Forced-cleanup confirmation is bounded to five seconds,
followed by a separate five-second drain bound. Natural execution remains bounded by its
configured task timeout. Bounds apply to asynchronous waits, not syscall/filesystem latency
or Application database callbacks.

Denied termination with a live group, an unavailable accounting query, delayed reference
release, root-exit observation failure or unsettled I/O raises `ProcessCleanupException`.
The same owner retains Job, exit evidence, logs, pending drains, chosen cause and original
acknowledgement exception. No terminal status is written. Retry only attempts cleanup and
collection; `attempted` prevents another launch. A drain timeout cancels reads and closes
read pipes; retry must still observe settled drains and reports collection failure.

Premature loss/closure of the actual Job handle is **not recoverable by querying saved PIDs**.
Kill-on-close may initiate OS termination, but the runner has lost its accounting proof and
keeps reporting unconfirmed cleanup. It never substitutes root exit or EOF. A separate
fixture process tests actual Job closure and repeated retained ownership; exiting that test
owner releases its remaining resources without inventing a terminal Run. Production owners
must keep the Job private and open until confirmation. An external actor with same-user
handle-duplication privileges remains outside this trusted-local-code boundary.

Failure saving Running/startup acknowledgement stops the entire group and settles collection
before propagating the original error. If cleanup also fails, the retained cleanup exception
comes first; successful retry surfaces the original SQLite/callback error. Terminal save
failure propagates after confirmed execution cleanup and does not become a fabricated Failed
or replay a launch. Logs/parameters/output directory keep their prior storage contract.

The test seam `IProcessTerminator.Stop(Action stopGroup)` now wraps the backend stop operation
instead of accepting a managed Process. A managed Process cannot represent a Job after root
handle release. Tests can still deny or ignore a stop; the runner always performs independent
confirmation. The internal Job-control seam provokes real invalid-buffer/invalid-handle
Win32 failures. The internal log-writer seam tests a real disposed FileStream write failure.
Neither changes the normal production path or adds a Domain/Application Win32 dependency.

## Verification and crash-test readiness

Real Windows tests cover a surviving descendant with inherited pipes and with closed pipes,
delayed descendant completion, late cancel, timeout after root exit, query/terminate failure,
cleanup retry without relaunch, actual premature Job closure, concurrent runs, log write
failure, exact/overflow byte caps, Unicode/JSON/arguments/CWD/output directory and startup/
terminal SQLite failures. Existing CLI tests include success, `fail=true` exit 7 and Ctrl+C.
Fixtures use bounded readiness/exit waits and only control processes created for the test.
The former root-only late-cancel assertion now respects the backend completion boundary:
after root exit, OS console teardown can still leave an associated process. Dedicated
descendant tests separately verify accepted cancellation while live and rejected intent
after confirmed successful group completion.

```powershell
dotnet build ResearchLab.slnx --no-restore
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.RunExecutionTests --filter-class ResearchLab.IntegrationTests.WindowsNativeLauncherTests
dotnet test --solution ResearchLab.slnx --no-build
dotnet format ResearchLab.slnx --no-restore --verify-no-changes
git diff --check
```

Final verification on 2026-10-25, Windows x64 / .NET 10: build passed with zero
warnings/errors; the full solution passed **281/281 tests, zero skips**; format verification
and diff whitespace checks passed. The M3.7b precondition was independently rerun before
implementation: 8/8 tests passed. Intermediate focused runs exposed fixture clock/runtime-file
setup errors and an obsolete root-only late-cancel assertion; these were corrected before
the final full run. Other Windows architectures/versions and Unix were not run here.

M3.7d now verifies the [real CLI crash/persistence matrix](windows-crash-safety.md).
The following was the remaining test boundary at the M3.7c checkpoint:
the production path was ready for M3.7d crash tests. The M3.7b separate-owner kill-on-close gate
remains, but this increment does not claim a complete CLI crash/recovery matrix. M3.7d should
kill the real owner around native creation/Running persistence/root exit/terminal persistence,
verify descendant termination and explicit Interrupted recovery without killing stored PIDs,
and test loss of the owner during retained cleanup. Recovery still records ownership loss,
not a reconstructed execution result or a database-backed proof of group termination.

References: [accounting layout/reference lifetime](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information),
[QueryInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject),
[TerminateJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-terminatejobobject),
[asynchronous termination and object lifetime](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess),
[Job inheritance, signaling and kill-on-close](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
