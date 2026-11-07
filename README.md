# ResearchLab

Локальная платформа для исследовательских экспериментов: импорт проекта → Experiment → Run → сохранённые логи и метрики. C# / .NET 10, модульный монолит с CLI, EF Core и SQLite.

## Состояние реализации

На момент ревизии 2026-11-07 `main` (HEAD `75305ed`) уже содержит реализацию: PR #1 объединил `chore/bootstrap` с `main`. Локальные ссылки `main` и `origin/chore/bootstrap` (`e89f84b`) имеют одинаковые деревья; `main` впереди на один merge-коммит. Это состояние локальных Git-ссылок, без обновления с сервера. Переключаться на `chore/bootstrap` для доступа к коду не нужно.

- `src/`: Domain, Application, Infrastructure, Execution и Cli.
- `tests/`: доменные и интеграционные тесты, синтетический ProcessFixture; `samples/foreground-demo/` — пример программы.
- Реализованы импорт и проверка доверия, неизменяемые снимки входных данных Run, SQLite-история, foreground-исполнение, stdout/stderr, отмена и таймауты, явное восстановление, приём и чтение структурированных метрик (M0–M3, M4.1–M4.4).
- На Windows используются native launch и Job Objects; восстановление фиксирует потерю владельца, но не доказывает остановку дочерних процессов.
- UI, регистрация неизменяемых артефактов, Research Story и HTML-публикация ещё не реализованы. Нет фонового сервиса или межпроцессной отмены. Импорт не запускает задачи; запуск требует явного доверия. Полная воспроизводимость среды не гарантируется.

## Сборка и запуск

Для чистого checkout с отслеживаемым `global.json`: .NET SDK 10.0.401 (`latestPatch`), Microsoft Testing Platform. Проекты используют `net10.0`.

```bash
git clone https://github.com/Discake/ResearchLab.git
cd ResearchLab
dotnet restore ResearchLab.slnx
dotnet build ResearchLab.slnx --no-restore
dotnet test --solution ResearchLab.slnx --no-build --no-restore
dotnet run --no-build --project src/ResearchLab.Cli -- --help
```

Сборка и тесты при ревизии документации не запускались. Сохранённые результаты относятся к Windows; Unix execution/locking остаются непроверенными.

[CLI quickstart (EN)](docs/cli-quickstart.md) / [RU](docs/cli-quickstart.ru.md) содержит PowerShell-сценарий импорта, запуска и восстановления. [Метрики (EN)](docs/metric-query.md) / [RU](docs/metric-query.ru.md) — чтение записанных измерений и smoke-сценарий.

## Документация

Начните с [индекса по функциональности](docs/README.md) и [карты кода и тестов](docs/CODEMAP.md); для агентов действуют [AGENTS.md](AGENTS.md).

- [VISION](VISION.md) / [RU](VISION.ru.md) — цели и границы продукта.
- [DOMAIN](DOMAIN.md) / [RU](DOMAIN.ru.md) — каноническая модель, включая ещё не реализованные области.
- [План реализации](IMPLEMENTATION_PLAN.md) — этапы, предложения и исторические отметки проверки.

Это инженерный прототип; полный путь до публикации пока не завершён.
