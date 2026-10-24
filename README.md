# ResearchLab

Local-first research platform connecting source code, experiment runs, recorded
evidence and publishable Research Stories. This repository currently implements
**M0 (Bootstrap) and M1 (domain core)**. It is not yet a runnable research application.

## Prerequisites and verification

- .NET SDK **10.0.401** (the SDK verified during bootstrap). `global.json` permits
  newer patches in the same 10.0.4xx feature band, but not preview SDKs.
- NuGet access for the initial test dependency restore.
- Git for development. No database, workloads, C++ compiler or Node.js required.

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
There is no runnable host or UI at M1.

## Structure and dependency boundaries

| Project | Responsibility | Project dependencies |
| --- | --- | --- |
| `src/ResearchLab.Domain` | Project, Experiment, parameters, Run snapshots/lifecycle and metrics | None |
| `src/ResearchLab.Application` | Empty boundary for future use cases | Domain |
| `tests/ResearchLab.Domain.Tests` | Domain invariants and architecture smoke test | Domain |

Production projects have no NuGet dependencies. The smoke test loads the built
Domain assembly and permits only `System.Runtime`, `System.Collections` and
`System.Linq`, guarding against accidental coupling to application, persistence
or web frameworks. It checks emitted assembly references, not unused project/package
references. No execution, filesystem or network operations occur in Domain.

The smaller initial structure is explicitly allowed by `IMPLEMENTATION_PLAN.md`.
Infrastructure, Execution and Web will be introduced when their stages need them:
Infrastructure and Execution reference Application and Domain; Web composes those
adapters and calls Application use cases. Domain must never reference outer layers.
No database, process runner, application use cases or UI are included.
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

Next: **M2 — persistence and project import**, including atomic optimistic
concurrency checks in storage, configuration parsing, path validation and explicit
task trust. Import must never execute project commands.

## Кратко по-русски

Реализованы M0 и M1: Domain с сущностями и инвариантами, Application → Domain
и тестовый проект. БД, исполнителя, прикладных сценариев и UI пока нет.
Нужен SDK 10.0.401 (допускаются более новые патчи 10.0.4xx) и доступ к NuGet.
Команды проверки приведены выше; предупреждения сборки считаются ошибками.
Тесты проверяют параметры, снимки Run, переходы состояний, версии и метрики.
Smoke-тест допускает только System.Runtime, System.Collections и System.Linq.
Использовано согласованное исправление EN/RU: ошибка запуска переводит Run
из Created сразу в Failed. Следующий этап — M2: хранение и импорт без автозапуска.
