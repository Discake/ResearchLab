# Code map

All displayed source/test paths are relative to the repository root; links resolve from this file. This map selects entry points and boundaries, not every file. Use the [documentation index](README.md) for statuses and Russian versions. Product/domain changes also require [VISION](../VISION.md), [DOMAIN](../DOMAIN.md) and applicable [decisions](decisions/README.md).

Dependency direction: Domain has no infrastructure dependency; Application owns use cases and interfaces; Infrastructure and Execution implement them; CLI is the composition root. Tests use synthetic fixtures and temporary stores/processes.

## Domain and Run invariants

Project/Experiment/Run aggregates, frozen inputs, terminal immutability, finite metrics and version checks.

- Contracts: [domain-core](domain-core.md), [run-recovery](run-recovery.md).
- Entry points: [src/ResearchLab.Domain/Run.cs](../src/ResearchLab.Domain/Run.cs), [src/ResearchLab.Domain/RunSnapshots.cs](../src/ResearchLab.Domain/RunSnapshots.cs), [src/ResearchLab.Domain/Experiment.cs](../src/ResearchLab.Domain/Experiment.cs), [src/ResearchLab.Domain/MetricPoint.cs](../src/ResearchLab.Domain/MetricPoint.cs).
- Contract checks: [tests/ResearchLab.Domain.Tests/RunLifecycleTests.cs](../tests/ResearchLab.Domain.Tests/RunLifecycleTests.cs), [tests/ResearchLab.Domain.Tests/SnapshotTests.cs](../tests/ResearchLab.Domain.Tests/SnapshotTests.cs), [tests/ResearchLab.Domain.Tests/ParameterTests.cs](../tests/ResearchLab.Domain.Tests/ParameterTests.cs), [tests/ResearchLab.Domain.Tests/MetricTests.cs](../tests/ResearchLab.Domain.Tests/MetricTests.cs).
- Neighbors: Application transitions, SQLite snapshot mapping and metric ingestion. Consult DOMAIN.md before changing invariants.

## Application orchestration

Prepare validated input, persist Created, recheck consent, acknowledge Running, retain execution ownership and persist evidence before completion.

- Contracts: [run-preparation](run-preparation.md), [managed-cancellation](managed-cancellation.md), [structured-execution](structured-execution.md).
- Entry points: [src/ResearchLab.Application/RunPreparation.cs](../src/ResearchLab.Application/RunPreparation.cs), [src/ResearchLab.Application/RunExecution.cs](../src/ResearchLab.Application/RunExecution.cs), [src/ResearchLab.Application/RunOperation.cs](../src/ResearchLab.Application/RunOperation.cs), [src/ResearchLab.Application/ProcessContracts.cs](../src/ResearchLab.Application/ProcessContracts.cs).
- Contract checks: [tests/ResearchLab.IntegrationTests/RunPreparationTests.cs](../tests/ResearchLab.IntegrationTests/RunPreparationTests.cs), [tests/ResearchLab.IntegrationTests/RunExecutionTests.cs](../tests/ResearchLab.IntegrationTests/RunExecutionTests.cs), [tests/ResearchLab.IntegrationTests/RunCancellationTests.cs](../tests/ResearchLab.IntegrationTests/RunCancellationTests.cs).
- Neighbors: Catalog/trust, Domain, session lifetime, process adapter and metric collector; persistence errors must not invent a terminal outcome.

## Execution and logs

Own one launch attempt, drain streams, cap output, arbitrate completion/timeout/cancellation and retain resources when cleanup is unconfirmed.

- Contracts: [minimal-execution](minimal-execution.md), [managed-cancellation](managed-cancellation.md), [local-run-completion](local-run-completion.md).
- Entry points: [src/ResearchLab.Execution/ProcessRunner.cs](../src/ResearchLab.Execution/ProcessRunner.cs), [src/ResearchLab.Execution/RunProcess.cs](../src/ResearchLab.Execution/RunProcess.cs), [src/ResearchLab.Execution/RunLogFiles.cs](../src/ResearchLab.Execution/RunLogFiles.cs), [src/ResearchLab.Application/RunLogs.cs](../src/ResearchLab.Application/RunLogs.cs).
- Contract checks: [tests/ResearchLab.IntegrationTests/RunExecutionTests.cs](../tests/ResearchLab.IntegrationTests/RunExecutionTests.cs), [tests/ResearchLab.IntegrationTests/RunCancellationTests.cs](../tests/ResearchLab.IntegrationTests/RunCancellationTests.cs), [tests/ResearchLab.IntegrationTests/RunHistoryTests.cs](../tests/ResearchLab.IntegrationTests/RunHistoryTests.cs).
- Neighbors: Application acknowledgement and terminal persistence, filesystem containment, Windows backend and stdout metric ingestion.

## Infrastructure, SQLite and import

Validate workspace/configuration and executable consent; inspect Git; persist aggregates with optimistic concurrency and migrations.

- Contracts: [persistence-import](persistence-import.md), [run-history](run-history.md).
- Entry points: [src/ResearchLab.Application/ProjectCatalog.cs](../src/ResearchLab.Application/ProjectCatalog.cs), [src/ResearchLab.Application/CatalogContracts.cs](../src/ResearchLab.Application/CatalogContracts.cs), [src/ResearchLab.Infrastructure/Catalog/WorkspaceInspector.cs](../src/ResearchLab.Infrastructure/Catalog/WorkspaceInspector.cs), [src/ResearchLab.Infrastructure/Catalog/WorkspacePaths.cs](../src/ResearchLab.Infrastructure/Catalog/WorkspacePaths.cs), [src/ResearchLab.Infrastructure/Persistence/SqliteResearchStore.cs](../src/ResearchLab.Infrastructure/Persistence/SqliteResearchStore.cs), [src/ResearchLab.Infrastructure/Persistence/ResearchDbContext.cs](../src/ResearchLab.Infrastructure/Persistence/ResearchDbContext.cs), [src/ResearchLab.Infrastructure/Persistence/Migrations](../src/ResearchLab.Infrastructure/Persistence/Migrations).
- Contract checks: [tests/ResearchLab.IntegrationTests/ImportTests.cs](../tests/ResearchLab.IntegrationTests/ImportTests.cs), [tests/ResearchLab.IntegrationTests/GitTests.cs](../tests/ResearchLab.IntegrationTests/GitTests.cs), [tests/ResearchLab.IntegrationTests/PersistenceTests.cs](../tests/ResearchLab.IntegrationTests/PersistenceTests.cs), [tests/ResearchLab.IntegrationTests/RunHistoryTests.cs](../tests/ResearchLab.IntegrationTests/RunHistoryTests.cs).
- Neighbors: Domain snapshot serialization, launch-time trust, session ownership and metric persistence/query. Import never executes declared tasks.

## Metric ingestion and query

Parse typed events, ingest in order with bounded batches, flush before terminal state, and read saved points through bounded read-only pages.

- Contracts: [metric-protocol](metric-protocol.md), [metric-ingestion](metric-ingestion.md), [structured-execution](structured-execution.md), [metric-query](metric-query.md).
- Entry points: [src/ResearchLab.Application/MetricProtocol.cs](../src/ResearchLab.Application/MetricProtocol.cs), [src/ResearchLab.Application/RunMetricCollector.cs](../src/ResearchLab.Application/RunMetricCollector.cs), [src/ResearchLab.Application/RunMetricIngestion.cs](../src/ResearchLab.Application/RunMetricIngestion.cs), [src/ResearchLab.Application/RunMetrics.cs](../src/ResearchLab.Application/RunMetrics.cs), [src/ResearchLab.Execution/StructuredStdout.cs](../src/ResearchLab.Execution/StructuredStdout.cs), [src/ResearchLab.Infrastructure/Persistence/SqliteMetricIngestion.cs](../src/ResearchLab.Infrastructure/Persistence/SqliteMetricIngestion.cs), [src/ResearchLab.Infrastructure/Persistence/SqliteRunMetrics.cs](../src/ResearchLab.Infrastructure/Persistence/SqliteRunMetrics.cs).
- Contract checks: [tests/ResearchLab.IntegrationTests/MetricProtocolTests.cs](../tests/ResearchLab.IntegrationTests/MetricProtocolTests.cs), [tests/ResearchLab.IntegrationTests/RunMetricIngestionTests.cs](../tests/ResearchLab.IntegrationTests/RunMetricIngestionTests.cs), [tests/ResearchLab.IntegrationTests/StructuredExecutionTests.cs](../tests/ResearchLab.IntegrationTests/StructuredExecutionTests.cs), [tests/ResearchLab.IntegrationTests/MetricQueryTests.cs](../tests/ResearchLab.IntegrationTests/MetricQueryTests.cs).
- Neighbors: Domain units/order rules, stdout bounds, Run version arbitration and CLI JSON. Choose parser, ingestion or query documentation first; read structured-execution for end-to-end changes.

## CLI

Compose services, parse commands, review trust, drive foreground attempts, expose history/logs/metrics and explicit recovery.

- Contracts: [console-host](console-host.md), [foreground-run](foreground-run.md), [cli-quickstart](cli-quickstart.md).
- Entry points: [src/ResearchLab.Cli/Program.cs](../src/ResearchLab.Cli/Program.cs), [src/ResearchLab.Cli/ConsoleHost.cs](../src/ResearchLab.Cli/ConsoleHost.cs), [src/ResearchLab.Cli/ForegroundRun.cs](../src/ResearchLab.Cli/ForegroundRun.cs), [src/ResearchLab.Cli/CommandArguments.cs](../src/ResearchLab.Cli/CommandArguments.cs).
- Contract checks: [tests/ResearchLab.IntegrationTests/ConsoleHostTests.cs](../tests/ResearchLab.IntegrationTests/ConsoleHostTests.cs), [tests/ResearchLab.IntegrationTests/ForegroundCliTests.cs](../tests/ResearchLab.IntegrationTests/ForegroundCliTests.cs), [scripts/metric-smoke.ps1](../scripts/metric-smoke.ps1).
- Neighbors: Application APIs, storage open mode, stdout/stderr and exit-code contracts. The smoke script executes a fixture only after trust confirmation.

## Recovery and cancellation ownership

Recover only proven ended sessions; preserve evidence and terminal outcomes. Waiting cancellation does not abandon the owner; cleanup retry keeps the same attempt.

- Contracts: [run-recovery](run-recovery.md), [managed-cancellation](managed-cancellation.md), [local-run-completion](local-run-completion.md).
- Entry points: [src/ResearchLab.Application/RunRecovery.cs](../src/ResearchLab.Application/RunRecovery.cs), [src/ResearchLab.Application/RunOperation.cs](../src/ResearchLab.Application/RunOperation.cs), [src/ResearchLab.Infrastructure/Persistence/SqliteRunSessions.cs](../src/ResearchLab.Infrastructure/Persistence/SqliteRunSessions.cs), [src/ResearchLab.Infrastructure/Persistence/Migrations/20261024164818_RunExecutionSessions.cs](../src/ResearchLab.Infrastructure/Persistence/Migrations/20261024164818_RunExecutionSessions.cs).
- Contract checks: [tests/ResearchLab.IntegrationTests/RunRecoveryTests.cs](../tests/ResearchLab.IntegrationTests/RunRecoveryTests.cs), [tests/ResearchLab.IntegrationTests/RunSessionLifetimeTests.cs](../tests/ResearchLab.IntegrationTests/RunSessionLifetimeTests.cs), [tests/ResearchLab.IntegrationTests/RunCancellationTests.cs](../tests/ResearchLab.IntegrationTests/RunCancellationTests.cs).
- Neighbors: Domain.Interrupt, RunExecution registration, CLI run recover and metric version conflicts. Recovery neither kills saved PIDs nor certifies child termination.

## Windows process lifecycle and crash boundary

Create directly in a private kill-on-close Job. Completion requires root exit/code, zero active Job processes and settled collection; non-Windows has weaker root/best-effort semantics.

- Contracts: [windows-native-launcher](windows-native-launcher.md), [windows-group-lifecycle](windows-group-lifecycle.md), [windows-crash-safety](windows-crash-safety.md).
- Entry points: [src/ResearchLab.Execution/WindowsNativeProcessLauncher.cs](../src/ResearchLab.Execution/WindowsNativeProcessLauncher.cs), [src/ResearchLab.Execution/WindowsNativeMethods.cs](../src/ResearchLab.Execution/WindowsNativeMethods.cs), [src/ResearchLab.Execution/RunProcess.cs](../src/ResearchLab.Execution/RunProcess.cs), [src/ResearchLab.Execution/ProcessRunner.cs](../src/ResearchLab.Execution/ProcessRunner.cs).
- Contract checks: [tests/ResearchLab.IntegrationTests/WindowsNativeLauncherTests.cs](../tests/ResearchLab.IntegrationTests/WindowsNativeLauncherTests.cs), [tests/ResearchLab.IntegrationTests/WindowsGroupExecutionTests.cs](../tests/ResearchLab.IntegrationTests/WindowsGroupExecutionTests.cs), [tests/ResearchLab.IntegrationTests/WindowsCrashSafetyTests.cs](../tests/ResearchLab.IntegrationTests/WindowsCrashSafetyTests.cs), [tests/ResearchLab.ProcessFixture/CrashFixture.cs](../tests/ResearchLab.ProcessFixture/CrashFixture.cs).
- Neighbors: Foreground owner lifetime, log drains, startup/terminal persistence failure windows and recovery. Crash tests are evidence for their Windows scope, not a new recovery authority.

For a recovery change, start at the recovery section, read run-recovery plus managed-cancellation (add local-run-completion for CLI behavior), then inspect RunRecovery, SqliteRunSessions and their tests. Expand to Windows crash tests only when the change touches process ownership/crash guarantees. No full sequential repository read is required.
