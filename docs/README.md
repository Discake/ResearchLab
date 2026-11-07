# Engineering documentation

Start with the area below and choose 1–3 relevant documents; use [CODEMAP](CODEMAP.md) to locate source, tests and neighboring boundaries. Expand as needed. English specifications are canonical; every existing technical guide below links its Russian version.

**Current contract** describes the implemented slice, not the entire planned product. **Proposal** describes future or superseded design. **Historical evidence** records a dated verification, not a test run on your checkout. Verification sections inside current contracts are also historical; their counts and OS coverage must not be generalized.

Root references: [developer README](../README.md), [agent rules](../AGENTS.md), [VISION](../VISION.md) / [RU](../VISION.ru.md) (product requirements), [DOMAIN](../DOMAIN.md) / [RU](../DOMAIN.ru.md) (domain requirements, including future entities), [implementation plan](../IMPLEMENTATION_PLAN.md) (proposal plus milestone history). These requirements are not a claim that UI, artifacts or publication already exist.

## Architecture and domain

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [domain-core](domain-core.md) / [RU](domain-core.ru.md) | Current contract (Domain scope) | Identities, parameters, snapshots and metrics; recovery extends the original lifecycle. |
| [run-lifecycle-design](run-lifecycle-design.md) / [RU](run-lifecycle-design.ru.md) | Historical proposal | Original executor design; use managed-cancellation and run-recovery instead. |

## Storage and project import

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [persistence-import](persistence-import.md) / [RU](persistence-import.ru.md) | Current contract (M2 scope) | SQLite metadata, task configuration, path checks and consent; later session/metric contracts extend it. |
| [run-preparation](run-preparation.md) / [RU](run-preparation.ru.md) | Current contract | Validation and immutable preparation before Run insertion. |

## Process execution and Run lifecycle

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [minimal-execution](minimal-execution.md) / [RU](minimal-execution.ru.md) | Current contract | Trusted launch, bounded logs and completion; structured-execution adds metrics. |
| [run-history](run-history.md) / [RU](run-history.ru.md) | Current contract | Bounded history and Run details. |
| [local-run-completion](local-run-completion.md) / [RU](local-run-completion.ru.md) | Current contract | Explicit CLI recovery and per-Run output directory; no artifact registration. |

## Cancellation, timeouts and recovery

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [managed-cancellation](managed-cancellation.md) / [RU](managed-cancellation.ru.md) | Current contract | Caller-owned RunOperation, cancellation arbitration and retained cleanup. |
| [run-recovery](run-recovery.md) / [RU](run-recovery.ru.md) | Current contract | Exclusive session evidence and Created/Running → Interrupted; no PID killing. |
| [windows-native-launcher](windows-native-launcher.md) / [RU](windows-native-launcher.ru.md) | Current contract; dated verification is historical | Native launch and handle ownership; windows-group-lifecycle defines production completion. |
| [windows-group-lifecycle](windows-group-lifecycle.md) / [RU](windows-group-lifecycle.ru.md) | Current contract; dated verification is historical | Job containment, group completion and cleanup retry; crash evidence is separate. |

## Metrics and structured protocol

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [metric-protocol](metric-protocol.md) / [RU](metric-protocol.ru.md) | Current contract | M4.1 byte-line parser and bounded validation. |
| [metric-ingestion](metric-ingestion.md) / [RU](metric-ingestion.ru.md) | Current contract | M4.2 ordered persistence and terminal flush. |
| [structured-execution](structured-execution.md) / [RU](structured-execution.ru.md) | Current contract | M4.3 real stdout ingestion, diagnostics and failure handling. |
| [metric-query](metric-query.md) / [RU](metric-query.ru.md) | Current contract; dated verification is historical | M4.4 read-only paginated queries and CLI smoke scenario. |

## CLI and user scenarios

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [console-host](console-host.md) / [RU](console-host.ru.md) | Current contract (metadata reads) | Composition root, read commands, JSON and exit codes; foreground-run adds writes/execution. |
| [foreground-run](foreground-run.md) / [RU](foreground-run.ru.md) | Current contract / usage guide | Trust review, foreground execution and Ctrl+C. |
| [cli-quickstart](cli-quickstart.md) / [RU](cli-quickstart.ru.md) | Current usage guide | PowerShell walkthrough from import to recovery. |

## Security and verification

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [windows-crash-safety](windows-crash-safety.md) / [RU](windows-crash-safety.ru.md) | Historical evidence | Real Windows CLI crash matrix; runtime contract is windows-group-lifecycle, recovery is run-recovery. |
| [m3-final-verification](m3-final-verification.md) / [RU](m3-final-verification.ru.md) | Historical evidence | Dated M3 Windows verification and platform limits; later M4 results appear in metric-query. |

## Future capabilities and proposals

| Document | Status | Purpose / follow-up |
| --- | --- | --- |
| [research-story-concept](research-story-concept.md) / [RU](research-story-concept.ru.md) | Proposal | Evidence-backed publications; not implemented. |
| [visualization-roadmap](visualization-roadmap.md) / [RU](visualization-roadmap.ru.md) | Proposal | Future scientific views and charts; not implemented. |
| [commercial-research-roadmap](commercial-research-roadmap.md) / [RU](commercial-research-roadmap.ru.md) | Proposal | Future product direction; not an implementation mandate. |

Security is cross-cutting: start with [agent rules](../AGENTS.md), [import/trust](persistence-import.md), and the execution/recovery contract for the boundary being changed. Job containment is not a security sandbox.

[Recorded metric smoke JSON](metric-query-actual.json) is historical evidence associated with metric-query, not a fixture to present as a new measurement. [ADR guidance](decisions/README.md) provides a minimal basis for future decisions; no retrospective decisions are asserted.
