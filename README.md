# ResearchLab

Local-first research platform connecting source code, experiment runs, recorded
evidence and publishable Research Stories. This repository currently implements
**M0–M3: bootstrap, domain core, SQLite/import and trusted local execution**.
M3 includes preparation, trusted foreground execution, managed cancellation, local recovery and verified Windows Job containment. The application API
is usable from .NET code and the foreground CLI. A UI is not implemented yet.

## Prerequisites and verification

- .NET SDK **10.0.401** (the SDK verified during bootstrap). `global.json` permits
  newer patches in the same 10.0.4xx feature band, but not preview SDKs.
- NuGet access for the initial dependency restore.
- Git CLI for development and Git integration tests. Import also supports non-Git
  workspaces and unavailable Git, recording unknown provenance.
- SQLite is bundled through EF Core; no database server, workloads, C++ compiler
  or Node.js required.

From the repository root:

```sh
dotnet restore ResearchLab.slnx
dotnet build ResearchLab.slnx --no-restore
dotnet test --solution ResearchLab.slnx --no-build --no-restore
dotnet format ResearchLab.slnx --verify-no-changes --no-restore
```

The solution targets `net10.0`. Nullable reference types and implicit usings are
enabled; compiler and analyzer warnings fail the build. Tests use xUnit v3 with
Microsoft Testing Platform, selected in `global.json` for the .NET 10 test runner.
See the [xUnit v3 setup guide](https://xunit.net/docs/getting-started/v3/cmdline).
Integration tests use real temporary SQLite databases and synthetic Git repositories.
Windows tests use directory junctions; Unix tests use symbolic links. The test suite
has been executed on Windows; Unix execution has not yet been verified.

To work on migrations (the pinned local tool is not needed for ordinary tests):

```sh
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/ResearchLab.Infrastructure
```

## Read existing data from the console

Build first using the commands above, then display help:

```powershell
dotnet run --no-build --project src/ResearchLab.Cli -- --help
```

The following PowerShell examples use an **existing ResearchLab database** containing
at least one project, experiment and Run. Set the two paths to your own existing storage;
no example database is shipped. Explicit `database init`, import/create and start commands
are described in the foreground guide below. `--data-dir` is exactly the private
log root previously supplied to `RunLogFiles`, with no added subdirectory.
These four commands are exercised against synthetic SQLite data by subprocess tests.

```powershell
$db = 'C:\ResearchLabData\research.db'
$dataDir = 'C:\ResearchLabData\logs'

$projects = dotnet run --no-build --project src/ResearchLab.Cli -- --database "$db" --data-dir "$dataDir" project list | ConvertFrom-Json
$projects
$projectId = $projects[0].id

$experiments = dotnet run --no-build --project src/ResearchLab.Cli -- --database "$db" --data-dir "$dataDir" experiment list --project $projectId | ConvertFrom-Json
$experiments
$experimentId = $experiments[0].id

$runs = dotnet run --no-build --project src/ResearchLab.Cli -- --database "$db" --data-dir "$dataDir" run list --experiment $experimentId --offset 0 --page-size 25 | ConvertFrom-Json
$runs
$runId = $runs[0].id

dotnet run --no-build --project src/ResearchLab.Cli -- --database "$db" --data-dir "$dataDir" run show $runId
```

Lists return JSON arrays (`[]` is a successful empty list); show returns a JSON object,
including safe diagnostics and known relative log paths. Run history is paged by Application
(50 items by default, maximum 100). Errors go to stderr: exit codes are 0 success, 2 usage,
3 missing record, 4 storage/data error, 130 canceled, 1 unexpected failure. In PowerShell,
inspect `$LASTEXITCODE` after invoking the program. These reads open SQLite read-only,
without initialization/migrations, owner sessions, recovery or task execution.
The equivalent built executable is
`src/ResearchLab.Cli/bin/Debug/net10.0/researchlab.exe` on Windows, or use
`dotnet src/ResearchLab.Cli/bin/Debug/net10.0/researchlab.dll` with the same arguments.

See [console contract](docs/console-host.md) / [контракт консольного host](docs/console-host.ru.md)
for configuration, data safety and exit codes.

## Start a real experiment from the console

The [foreground guide](docs/foreground-run.md) / [запуск через CLI](docs/foreground-run.ru.md)
walks from empty storage through explicit `database init`, `project import` and
`experiment create` to `run start <experiment-id> --parameters <json-object>` using
the supplied real console example. Each start displays the executable/configuration
fingerprint and requires `yes` in that invocation; trust is never persisted.
Ctrl+C requests owned cancellation and waits for cleanup and terminal persistence.
`run show <run-id> --include-logs` reopens the saved result and bounded log text later.

## Structure and dependency boundaries

| Project | Responsibility | Project dependencies |
| --- | --- | --- |
| `src/ResearchLab.Domain` | Project, Experiment, parameters, Run snapshots/lifecycle and metrics | None |
| `src/ResearchLab.Application` | Catalog, preparation/execution use cases, session consent and boundary interfaces | Domain |
| `src/ResearchLab.Infrastructure` | EF Core/SQLite, migrations, filesystem/configuration and bounded Git inspection | Application, Domain |
| `src/ResearchLab.Execution` | Minimal bounded local ProcessRunner | Application |
| `src/ResearchLab.Cli` | Read-only console adapter and composition root | Application, Infrastructure, Execution |
| `tests/ResearchLab.Domain.Tests` | Domain invariants and architecture smoke test | Domain |
| `tests/ResearchLab.IntegrationTests` | Persistence, import, trust, paths, Git, execution and console reads | Infrastructure, Execution, Cli |
| `tests/ResearchLab.ProcessFixture` | Synthetic subprocess and crash owner for tests | Infrastructure, Execution |

Domain and Application have no NuGet dependencies. Infrastructure uses EF Core
SQLite 10.0.12, plus its design-time package for migrations. The smoke test loads the built
Domain assembly and permits only `System.Runtime`, `System.Collections` and
`System.Linq`, guarding against accidental coupling to application, persistence
or web frameworks. It checks emitted assembly references, not unused project/package
references. No execution, filesystem or network operations occur in Domain.

The smaller initial structure is explicitly allowed by `IMPLEMENTATION_PLAN.md`.
Execution references Application; Web remains deferred and will compose the
adapters and calls Application use cases. Domain must never reference outer layers.
No UI is included. Git inspection launches only the host Git
CLI, never a declared research task; repository fsmonitor and external filters are disabled.
CI automation is deferred; the commands above are the local verification baseline.

## Specifications and next increment

- [Agent instructions](AGENTS.md)
- [Vision](VISION.md) / [Видение](VISION.ru.md)
- [Domain](DOMAIN.md) / [Предметная модель](DOMAIN.ru.md)
- [Implementation plan](IMPLEMENTATION_PLAN.md)

M1 implements typed IDs, strict parameter validation, immutable Run input snapshots,
version-checked lifecycle changes (including `Created → Failed`) and append-only
metrics. See [domain implementation notes](docs/domain-core.md) /
[описание реализации](docs/domain-core.ru.md) for API rules and remaining boundaries.
All test data is synthetic fixture data, not experimental evidence.

M2 adds import, create/list/reopen Experiment, migrations and version-checked Run
persistence. See [M2 API usage and configuration](docs/persistence-import.md) /
[M2: использование API и конфигурация](docs/persistence-import.ru.md).
Import never executes project commands. Consent is explicit and session-local.

M3 now includes a minimal trusted ProcessRunner and persisted startup/completion states.
See [execution API and limitations](docs/minimal-execution.md) /
[исполнение и ограничения](docs/minimal-execution.ru.md). Bounded stdout/stderr logs are
retained per Run and available through Application.
Run details and bounded history pages are available through Application:
[RunHistory API](docs/run-history.md) / [чтение истории Run](docs/run-history.ru.md).
M3.3 adds explicit, idempotent recovery of Created/Running from verified ended owner sessions:
[recovery contract](docs/run-recovery.md) / [контракт восстановления](docs/run-recovery.ru.md).
Active and unknown owners are skipped; no process is killed. M4 artifact storage remains a future increment.

## Кратко по-русски

Реализованы M0–M3: доменное ядро, SQLite/EF Core, импорт, каталог экспериментов
и явное подтверждение доверия. M3 дополнен подготовкой и минимальным доверенным исполнением
с сохранением состояний Run и раздельных ограниченных логов stdout/stderr.
Доступ к логам, деталям Run и страницам истории предоставляется через Application.
M3.3 восстанавливает Created/Running подтверждённо завершённых сеансов в Interrupted;
активные/неизвестные владельцы не затрагиваются, процессы не завершаются.
API доступен из .NET и foreground CLI; UI пока нет.
Команды `project list`, `experiment list --project <id>`, `run list --experiment <id>`
и `run show <id>` требуют явных `--database` и `--data-dir`. Чтение открывает БД read-only,
без миграций, recovery и запуска задач. Для подготовки и запуска с нуля:
[foreground-руководство](docs/foreground-run.ru.md). `run start` каждый раз требует review
и явное yes; Ctrl+C запрашивает отмену с ожиданием терминального сохранения.
Нужен SDK 10.0.401 (допускаются более новые патчи 10.0.4xx) и доступ к NuGet.
Команды проверки приведены выше; предупреждения сборки считаются ошибками.
Тесты проверяют параметры, снимки Run, переходы состояний, версии и метрики.
Smoke-тест допускает только System.Runtime, System.Collections и System.Linq.
Использовано согласованное исправление EN/RU: ошибка запуска переводит Run
из Created сразу в Failed. Интеграционные тесты используют реальные временные БД
и Git-репозитории; проверены на Windows. Импорт не запускает задачи. Следующий этап — M4.

M3.6 adds `run recover` and `<data-dir>/<RunId:N>/output` via `RESEARCHLAB_OUTPUT_DIR`, preserving workspace CWD. [Contract and remaining limits](docs/local-run-completion.md).

M3.6 добавляет `run recover` и каталог результатов каждого Run через `RESEARCHLAB_OUTPUT_DIR`, сохраняя CWD проекта. [Контракт и оставшиеся ограничения](docs/local-run-completion.ru.md).

M3.7b introduced [native Windows Job launch](docs/windows-native-launcher.md). M3.7c now uses it in ProcessRunner/CLI: root exit, saved exit code, zero active Job processes and settled collection are required before completion. Cancel/timeout stop the whole Job, including descendants of an exited root; unconfirmed cleanup retains ownership for retry. [Group contract and crash-test boundary](docs/windows-group-lifecycle.md).

M3.7b добавил [native Windows Job launch](docs/windows-native-launcher.ru.md). M3.7c подключает его к ProcessRunner/CLI: требуются выход корня, сохранённый exit code, ноль активных процессов Job и завершённый сбор. Cancel/timeout останавливают весь Job, включая потомков завершившегося корня; неподтверждённая очистка сохраняет владельца для повтора. [Групповой контракт и граница crash tests](docs/windows-group-lifecycle.ru.md).

M3.7d verifies CLI-only forced termination, startup/terminal persistence windows, independent concurrent Jobs, explicit recovery and compatible nested/console contexts with real Windows process handles. 21 crash cases and all 302 solution tests passed on the documented Windows build. Checkpoints exist only in a separate CrashTests build. [Observed matrix and limitations](docs/windows-crash-safety.md).

M3.7d проверяет принудительное завершение только CLI, startup/terminal окна сохранения, независимые конкурентные Job, явный recovery и допустимые nested/console-контексты настоящими Windows process handles. 21 crash case и все 302 теста solution прошли на указанном в отчёте Windows build. Checkpoints есть только в отдельной сборке CrashTests. [Наблюдаемая матрица и ограничения](docs/windows-crash-safety.ru.md).

M3.7e final verification (2026-10-25): restore, build, all 302 tests (zero skips), format
and EF pending-model check passed. Verified platform: Windows 11 25H2 x64 build 26200.9711;
other Windows versions and Unix are unverified. M4 can begin within this scope.
See [demo, regression results and guarantee limits](docs/m3-final-verification.md), including
the manual Ctrl+C verification gap and PowerShell 5.1 JSON limitations.

M3.7e (2026-10-25): restore, build, все 302 теста без пропусков, format и EF model check
прошли. Проверена Windows 11 25H2 x64 build 26200.9711; другие Windows и Unix не проверены.
Можно начинать M4 в этих границах. [Demo, регрессии и ограничения](docs/m3-final-verification.ru.md),
включая отсутствие ручного Ctrl+C и ограничения JSON в PowerShell 5.1.
