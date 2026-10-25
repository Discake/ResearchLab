# M3.7b — isolated Windows native launcher

`WindowsNativeProcessLauncher` is internal to Execution. M3.7b introduced it separately;
M3.7c now connects it to ProcessRunner and the CLI with [group completion](windows-group-lifecycle.md).
Domain, Application lifecycle, recovery and SQLite are unchanged. This page describes
the low-level launch/resource-ownership component, not the full runner lifecycle.

## Launch contract

The caller supplies an already reviewed absolute `.exe` path, literal arguments, validated
workspace CWD, serialized parameters JSON and an allocated absolute output directory.
The launcher does not repeat catalog trust or filesystem containment checks. It never
searches PATH or invokes a shell. `lpApplicationName` is explicit, `lpCommandLine` is a
mutable Unicode buffer, and the environment is a case-insensitively sorted, double-NUL
terminated Unicode block inheriting the current environment and replacing only
`RESEARCHLAB_PARAMETERS_JSON` and `RESEARCHLAB_OUTPUT_DIR`.

Arguments are quoted using Microsoft CRT/.NET backslash/quote rules, including empty
arguments and trailing backslashes. This is verified with an actual .NET apphost, including
Unicode and JSON, rather than assumed to work with every custom executable parser.
NUL and command lines beyond the Windows limit are rejected before process creation.

Each launch first creates an unnamed, noninheritable Job and sets only
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; neither breakaway flag is enabled. `CreateProcessW`
receives `STARTUPINFOEX`, `PROC_THREAD_ATTRIBUTE_JOB_LIST` and
`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. The latter contains exactly stdin/stdout/stderr child
ends, and requires `bInheritHandles=true`. There is no post-start assignment or fallback.
Job setup, unsupported attributes, ambient nested-Job restrictions or CreateProcess
failure prevent launch. The optional internal Job inspection callback exists solely for
tests to inspect the prepared Job or provoke a real Win32 preparation failure.

stdin is a pipe whose parent write end is closed on return (EOF). stdout/stderr are
separate byte streams backed by noninheritable overlapped named-pipe read handles; their
temporary names are random and access is restricted to the current user. Pending reads
support cancellation. Encoding belongs to the child/caller; the synthetic fixture explicitly
writes UTF-8. Callers must drain both streams concurrently and apply their own byte/time caps.

## Resource ownership

The owner and empty process/thread SafeHandles are allocated before CreateProcess. On success,
the returned handles and PID are adopted immediately; there are no subsequent membership
queries, callbacks or fallible managed setup steps. The primary thread handle closes at once.
Temporary child pipe ends, stdin writer, attribute list and environment buffer close after
creation. Attribute values remain allocated until DeleteProcThreadAttributeList; their handles
remain alive throughout. Partial setup failure releases everything already allocated.

The returned `WindowsNativeProcess` retains Job/process handles and both parent streams.
`WaitForRootExitAsync(token)` polls the owned process handle without blocking a worker,
confirms it is signaled, then reads the real exit code (including 259). Cancellation ends
only the wait and retains ownership. It does not confirm descendants or pipe EOF.
Dispose closes the Job, requesting termination of its remaining processes, then releases
handles/streams; it does **not** wait for or certify group completion. Lifetime is owned by
one caller: do not race Dispose against waits/reads; cancel and settle reads first.

Concurrent launches through this component use distinct explicit handle lists. This does
not coordinate unrelated host code using unrestricted handle inheritance: a simultaneous
legacy CreateProcess with broad inheritance could inherit temporarily inheritable pipe
child ends. The Job handle itself is never inheritable. Trusted local code can still access
the user's filesystem and OS privileges; this is process containment, not a security sandbox.

## Verification and next boundary

Windows integration tests cover success/nonzero/259 exits, a space/Unicode executable path,
literal arguments, environment replacement/inheritance, CWD, closed stdin, split and large
streams, canceled reads/waits, exact Job membership/flags and noninheritance, real Job setup
failure, invalid PE creation failure, concurrent independence/EOF and isolated-owner handle
counts after repeated failures and concurrent successful launches. Readiness uses bounded
acknowledgements. The crash gate kills only a separate test owner and confirms that its
held root **and descendant** exit; cleanup only targets explicitly created fixture processes.
Non-Windows hosts skip these tests explicitly. Windows x64 is the verified platform;
x86/ARM64 and other supported Windows versions require their own runs.

```powershell
dotnet build ResearchLab.slnx --no-restore
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.WindowsNativeLauncherTests
dotnet test --solution ResearchLab.slnx --no-build
dotnet format ResearchLab.slnx --no-restore --verify-no-changes
```

M3.7b checkpoint on 2026-10-25, Windows x64 / .NET 10: build passed with zero warnings/errors;
8/8 native-launch tests and 267/267 solution tests passed without skips; format verification
passed. The initial sandbox test attempt failed at the test runner's IPC pipe before any
tests ran; the successful runs used execution outside that sandbox.

M3.7c implements [group completion and production integration](windows-group-lifecycle.md).
Root exit, Job close and EOF remain separate evidence; direct low-level callers must not
confuse this component's root wait with the runner's group-completion barrier.

## Official API references

- [CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)
- [STARTUPINFOEXW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-startupinfoexw), [STARTUPINFOW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow)
- [Attributes: JOB_LIST, HANDLE_LIST and lifetime](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Initialize attribute list](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-initializeprocthreadattributelist), [Delete attribute list](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-deleteprocthreadattributelist)
- [CreateJobObjectW](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-createjobobjectw), [SetInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-setinformationjobobject)
- [Extended limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information), [Basic limits and kill/breakaway flags](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
- [IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob), [WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject), [GetExitCodeProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess)
- [CRT argument parsing](https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments?view=msvc-170)
