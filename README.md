# ResearchLab

Local-first research platform connecting source code, experiment runs, recorded
evidence and publishable Research Stories. This repository currently implements
**M0–M2: bootstrap, domain core, SQLite persistence and local project import**.
M3 also includes preparation and minimal trusted local execution. The application API
is usable from .NET code; a runnable host/UI is not implemented yet.

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

## Structure and dependency boundaries

| Project | Responsibility | Project dependencies |
| --- | --- | --- |
| `src/ResearchLab.Domain` | Project, Experiment, parameters, Run snapshots/lifecycle and metrics | None |
| `src/ResearchLab.Application` | Catalog, preparation/execution use cases, session consent and boundary interfaces | Domain |
| `src/ResearchLab.Infrastructure` | EF Core/SQLite, migrations, filesystem/configuration and bounded Git inspection | Application, Domain |
| `src/ResearchLab.Execution` | Minimal bounded local ProcessRunner | Application |
| `tests/ResearchLab.Domain.Tests` | Domain invariants and architecture smoke test | Domain |
| `tests/ResearchLab.IntegrationTests` | Persistence, import, trust, paths, Git and process execution | Infrastructure, Execution |
| `tests/ResearchLab.ProcessFixture` | Synthetic subprocess for execution tests | None |

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
[исполнение и ограничения](docs/minimal-execution.ru.md). Advanced cancellation, stored
logs, output/artifact directories, history listing and recovery remain future increments.

## Кратко по-русски

Реализованы M0–M2: доменное ядро, SQLite/EF Core, импорт, каталог экспериментов
и явное подтверждение доверия. M3 дополнен подготовкой и минимальным доверенным исполнением
с сохранением состояний Run. API доступен из .NET; UI пока нет.
Нужен SDK 10.0.401 (допускаются более новые патчи 10.0.4xx) и доступ к NuGet.
Команды проверки приведены выше; предупреждения сборки считаются ошибками.
Тесты проверяют параметры, снимки Run, переходы состояний, версии и метрики.
Smoke-тест допускает только System.Runtime, System.Collections и System.Linq.
Использовано согласованное исправление EN/RU: ошибка запуска переводит Run
из Created сразу в Failed. Интеграционные тесты используют реальные временные БД
и Git-репозитории; проверены на Windows. Импорт не запускает задачи. Следующий этап — M3.
