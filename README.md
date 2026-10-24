# ResearchLab

Local-first research platform connecting source code, experiment runs, recorded
evidence and publishable Research Stories. This repository currently implements
**M0 (Bootstrap) only**, not a working research application.

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
There is no runnable host or UI at M0.

## Structure and dependency boundaries

| Project | Responsibility | Project dependencies |
| --- | --- | --- |
| `src/ResearchLab.Domain` | Empty boundary for future domain rules | None |
| `src/ResearchLab.Application` | Empty boundary for future use cases | Domain |
| `tests/ResearchLab.Domain.Tests` | Domain architecture smoke test; future invariant tests | Domain |

Production projects have no NuGet dependencies. The smoke test loads the built
Domain assembly and rejects dependencies other than `System.Runtime`, guarding
against accidental coupling to application, persistence or web frameworks.
This deliberately narrow allowlist can be reviewed when domain code needs more
standard library assemblies. It checks emitted assembly references, not unused
project/package references.

The smaller initial structure is explicitly allowed by `IMPLEMENTATION_PLAN.md`.
Infrastructure, Execution and Web will be introduced when their stages need them:
Infrastructure and Execution reference Application and Domain; Web composes those
adapters and calls Application use cases. Domain must never reference outer layers.
No placeholder entities, interfaces, database, process runner or UI are included.
CI automation is deferred; the commands above are the local verification baseline.

## Specifications and next increment

- [Agent instructions](AGENTS.md)
- [Vision](VISION.md) / [Видение](VISION.ru.md)
- [Domain](DOMAIN.md) / [Предметная модель](DOMAIN.ru.md)
- [Implementation plan](IMPLEMENTATION_PLAN.md)

Next: **M1 — domain core**, with typed IDs, parameter validation, immutable Run
snapshots and lifecycle tests. Before implementing lifecycle transitions, resolve
the existing specification ambiguity: startup failure is described as `Failed`,
but the diagram does not include `Created → Failed`. Both English and Russian
documents share this ambiguity; bootstrap does not change their contracts.

## Кратко по-русски

Реализован только M0: каркас Domain → без внешних зависимостей,
Application → Domain и тестовый проект. Сущностей, БД, исполнителя и UI пока нет.
Нужен SDK 10.0.401 (допускаются более новые патчи 10.0.4xx) и доступ к NuGet.
Команды проверки приведены выше; предупреждения сборки считаются ошибками.
Smoke-тест загружает Domain и проверяет зависимости скомпилированной сборки.
Следующий этап — M1; перед ним нужно согласовать переход при ошибке старта
процесса: текст EN/RU описывает `Failed`, но диаграмма не содержит
`Created → Failed`. Утверждённые спецификации не изменены.
