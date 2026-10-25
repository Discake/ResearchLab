# M4.3 — structured stdout during real execution

`RunExecution` now connects the real `ProcessRunner` to M4.1 parsing and M4.2 durable
metric ingestion. Domain, SQLite schema, executable trust, native Windows launch,
Job containment and recovery rules are unchanged. Artifacts and UI are not implemented.

## Sequence and ownership

```mermaid
sequenceDiagram
    participant C as Child / Windows Job
    participant R as ProcessRunner drains
    participant L as Private logs
    participant A as Application
    participant D as SQLite
    A->>D: Persist Created
    A->>R: Execute(onStarted, output sink)
    R->>C: Existing trusted native launch
    par Raw output begins
        C->>R: stdout bytes
        R->>L: Exact bytes within combined raw limit
        Note over R,A: metric ingestion awaits successful acknowledgement
    and Startup acknowledgement
        R->>A: onStarted(PID, time)
        A->>D: Run.Start + SaveRun (version check)
        A-->>R: Running durably acknowledged
    end
    loop Sequential stdout lines, with backpressure
        R->>A: Borrowed UTF-8 line + 1-based line number
        A->>A: M4.1 parse / M4.2 bounded batch
        A->>D: AddMetric batch + version-checked commit
        A-->>R: Safe diagnostic codes / line numbers
        R->>L: Bounded protocol.log
    end
    R->>A: stdout EOF / collection end
    A->>D: Final flush of accepted complete events
    C-->>R: Root exit AND Windows Job empty
    Note over R,L: Await both drains and flushes; no pending collector task
    R-->>A: ProcessResult
    A->>D: Terminal transition through current ingestion version
```

Raw stdout/stderr reads begin beside `onStarted`, as before. A single acknowledgement
barrier prevents typed metric ingestion until the Running write succeeds. Pure M4.1
parsing, ordinary log lines and parser diagnostics can proceed before acknowledgement.
Only a parsed metric awaits the barrier in Application, holding one bounded line/read
buffer; the child can then block on its bounded OS pipe. Ordinary logs keep draining,
including at M3 startup crash checkpoints.
Stderr drains independently. Application acknowledgement must not wait for child exit
or stdout completion: that would conflict with backpressure. Failed acknowledgement
opens the raw-drain barrier in discard-for-metrics mode, confirms cleanup, and propagates
the original failure. No metrics are written to Created.

`IProcessExecution` has a sink overload. Real ProcessRunner overrides it; existing
outputless adapters retain their original overload. Any stream-capable decorator must
forward the sink and preserve the acknowledgement/settled-collection contract.
`RunMetricCollector` owns parsing, bounded pending line numbers, ingestion and callback
failure capture. Execution never receives a Run, chooses a Run status, or uses DbContext.

Ingestion is created lazily at the first parsed metric after acknowledgement. It then
owns metric/terminal persistence via the same Run/version fence. The initial Run instance
is never saved with a stale version after metrics. Without metrics the existing M3
terminal path remains. RunOperation retains ownership on ProcessCleanupException;
retry resumes the same process/collection, never launches again.

## Bytes, bounds and diagnostics

- Original raw bytes go to stdout.log/stderr.log before structured processing. No
  decoding/re-encoding, removed protocol lines, BOM insertion or newline conversion.
- Only stdout is framed on LF. CRLF and a final unterminated line are supported.
  The framer retains at most 8195 bytes (8 KiB payload limit plus terminator/overflow
  detection), discarding oversized remainder until LF. The parser diagnoses an
  oversized protocol line once; long ordinary lines remain ordinary raw logs.
- Read buffers remain 8192 bytes per stream. M4.2 holds at most 128 typed events;
  line correlation holds at most 128 numbers. There is no unbounded history/cache,
  channel, background writer, scheduler or detached collection task.
- stdout delivery awaits Application work, providing backpressure. Stderr continues
  independently. Metrics flush on batch size, elapsed interval at a subsequent metric,
  and stdout completion. No idle-time flush timer was introduced.
- The existing **1 MiB combined stdout + stderr limit** is unchanged. Only retained
  bytes are considered for parsing. At cutoff/read failure, an incomplete trailing
  event is discarded rather than fabricated; already accepted complete events flush.
- The additional private `protocol.log` holds JSON Lines `{line, code}`: at most 256
  problem records plus one `DiagnosticsTruncated` marker. Codes are capped at 64
  characters; current codes are fixed ASCII parser/domain identifiers. No payload,
  metric name, unit, exception message, secret or absolute path is interpolated.
  Diagnostics are appended when detected; a deferred batch rejection may follow a
  later line's parser diagnostic. Line numbers still identify the original raw line.
- Read the file through `RunLogs.OpenReadAsync(id, RunLogStream.Protocol)` or inside
  `<data-dir>/<RunId:N>/protocol.log`. Existing stdout/stderr paths and CLI JSON remain
  compatible. Older/outputless attempts need not have a protocol file. Raw logs are
  private and can contain the child's secrets; diagnostics do not duplicate them.

Malformed/unsupported events and incompatible units are data diagnostics, not an
automatic Failed outcome. Valid neighbors retain their Sequence and are durably saved.
The known parser/domain diagnostic set bounds storage independently of raw log quota.

## Mandatory collection and failures

Final stdout collection calls M4.2 FlushAsync and writes its rejection diagnostics.
Only after Job/root completion, both drains and all collection writes have settled does
Application record Succeeded/Failed/Canceled/TimedOut. Root exit alone never completes
a Windows Run. Timeout and cancellation continue to use the existing owner and Job stop.
Accepted metrics survive nonzero exit, timeout and cancellation.

A raw/diagnostic collection failure prevents Succeeded. A diagnostic write failure can
retain the observations accepted before collection stopped and records Failed after
cleanup. Database/optimistic concurrency failures propagate to the workflow as their
original exception, even if a process adapter wraps or swallows the callback failure.
No terminal success is written; a durable Running Run may remain for explicit recovery.
Previously committed batches remain. Terminal persistence failure also propagates and
leaves already committed observations untouched. No automatic program retry occurs.

The existing five-second post-group collection cleanup limit still applies. A slow
pending database write can outlive that limit: ownership and pending I/O are retained
through ProcessCleanupException until collection settles. There is no terminal write
while it is running. Backpressure can affect child runtime and therefore its timeout.
Crash loss is limited to the current uncommitted batch; exact-once replay after crash
and idle flush scheduling are not implemented. Unix behavior uses the existing root /
best-effort tree backend and is not newly verified as Windows-style containment.

## Changed areas and evidence

Application: ProcessContracts, RunExecution, new RunMetricCollector, RunLogs enum.
Execution: ProcessRunner, RunLogFiles, new StructuredStdout.
Tests: new StructuredExecutionTests and MetricFixture, fixture Program routing.
Documentation: this EN/RU contract, M4.2 integration status and README.
No Domain, infrastructure/schema, CLI, Windows native/Job or recovery files changed.

Tests use actual native child fixtures and temporary SQLite, not fabricated experiment
evidence: exact raw bytes, byte-wise writes including UTF-8/CRLF, final EOF line,
concurrent stderr, invalid/unknown/unit/oversized events, bounded diagnostics,
2000 ordered points, raw quota, nonzero exit, timeout/cancel, pre-Running output,
SQLite Running/MetricPoint/terminal failure, swallowed callback failure, diagnostic
write failure and separate CLI start/reopen processes. Full regression includes the
existing Windows cancellation, group lifecycle, native-launch and crash tests.

## Verification — 2026-10-25

Final validation on this Windows workspace:

```text
dotnet build ResearchLab.slnx --no-restore
dotnet test --solution ResearchLab.slnx --no-build --no-restore
dotnet format ResearchLab.slnx --verify-no-changes --no-restore
dotnet ef migrations has-pending-model-changes --project src/ResearchLab.Infrastructure --no-build
git diff --check
```

All succeeded. Build: zero warnings/errors. Full suite: **412 passed, zero failures
or skips**, including **23 new structured execution cases** and existing Windows
crash/cancellation/native/group lifecycle tests. Full run: 1m22s. EF: no pending model
changes. Domain/schema/CLI/native launcher/recovery were not modified. Dependencies
and migrations were not added. No commit or push was made. Unix was not run.

Observed synthetic fixture outcomes:

| Case | Durable result |
| --- | --- |
| Two valid observations | Succeeded, values 0.5/0.2, Sequence 1/2, Version 4 |
| Invalid/unknown/incompatible-unit event between valid points | Succeeded, same two values/sequences, safe code at raw line 3 |
| Nonzero child exit | Failed, exit 7, both observations retained, Version 4 |
| Timeout / explicit cancellation | TimedOut / Canceled, all 130 emitted points retained, Version 132 |
| Metric INSERT 129 rejected by SQLite trigger | Original DbUpdateException, Running retained, first 128 committed points preserved |
| Running UPDATE rejected | Original DbUpdateException after confirmed process exit, Created with no metrics |
| Terminal UPDATE rejected | Original DbUpdateException, Running with both committed points |
| Separate CLI start then CLI show | Same Run ID and persisted MetricPoint identities after reopening |

Earlier validation exposed a barrier placed too early in the stdout drain: it blocked
ordinary output at pre-Running Windows crash checkpoints (408/411 passed). The final
barrier lives at typed metric consumption in Application; ordinary logs continue to
drain. A new 64 KiB pre-Running ordinary-output test guards this case, and the full
suite was rerun successfully. An initial fixture readiness signal after pipe flush
was moved to a single-write/no-flush marker so it does not depend on acknowledgement.
Focused structured tests progressed from 18/19 to 19/19 and then 22/22 before the final
23-case full-suite verification. Preliminary diff checks found extra EOF blank lines;
these were removed. Throughput benchmarking and forced crashes during a metric commit
were not performed; existing lifecycle crash tests did run.
