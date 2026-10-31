# ResearchLab

**ResearchLab** — локальная платформа для воспроизводимых вычислительных экспериментов: от регистрации проекта и конфигурации запуска до выполнения процесса, сохранения истории, логов и структурированных метрик. Проект реализуется на C# / .NET 10.

> **Где код?** Основная реализация и актуальная техническая документация находятся в ветке **[`chore/bootstrap`](https://github.com/Discake/ResearchLab/tree/chore/bootstrap)**. Ветка `main` сейчас содержит спецификации и планы, но не исходники приложения. Для клонирования, сборки и запуска переключитесь на `chore/bootstrap`.

## Реализованные возможности (chore/bootstrap)

- Многослойная архитектура: `Domain`, `Application`, `Infrastructure`, `Execution`, `Cli`.
- Доменная модель Project / Experiment / Run с типизированными идентификаторами и валидацией параметров.
- Хранение метаданных и истории в SQLite через EF Core; миграции и повторное открытие данных.
- Проверяемый импорт локальных проектов, работа с Git-метаданными и явное подтверждение доверия перед запуском исполняемого файла.
- Локальное выполнение процессов с отслеживанием состояний, stdout/stderr, отменой, таймаутами и сохранением результата.
- Управляемая обработка прерываний и явное восстановление записей об оставшихся незавершённых запусках; Windows Job Objects для группового жизненного цикла процессов.
- Сбор структурированных метрик и сохранение в SQLite, CLI для работы с проектами, экспериментами и запусками.
- Модульные и интеграционные тесты, включая сценарии с реальными временными БД и тестовыми процессами.

**Границы реализации:** код в `chore/bootstrap` описывает стадии M0–M3 и M4.1–M4.4. Графический интерфейс ещё не реализован, и полноценное хранение артефактов находится за пределами текущей реализации. Внутренний README рабочей ветки содержит более подробный перечень ограничений.

## Технологии

C# · .NET 10 · EF Core · SQLite · CLI · Git · xUnit v3 · Microsoft Testing Platform · Windows process management

## Сборка и проверка

```bash
git clone https://github.com/Discake/ResearchLab.git
cd ResearchLab
git switch chore/bootstrap
dotnet restore ResearchLab.slnx
dotnet build ResearchLab.slnx --no-restore
dotnet test --solution ResearchLab.slnx --no-build --no-restore
```

В `global.json` указан .NET SDK **10.0.401** с допуском новых patch-версий в рамках того же feature band. Тестовый запуск документирован для Windows; работа на Unix-системах не полностью подтверждена.

## Попробовать CLI

После сборки:

```bash
dotnet run --no-build --project src/ResearchLab.Cli -- --help
```

Для проверяемого локального сценария с созданием временного проекта, запуском эксперимента, чтением истории и восстановлением откройте [CLI quickstart](https://github.com/Discake/ResearchLab/blob/chore/bootstrap/docs/cli-quickstart.md). Пример содержит команды PowerShell и требует отдельного подтверждения перед запуском стороннего процесса.

## Документация

- [Подробный README в ветке разработки](https://github.com/Discake/ResearchLab/blob/chore/bootstrap/README.md)
- [Архитектура и доменная модель](https://github.com/Discake/ResearchLab/blob/chore/bootstrap/DOMAIN.md)
- [План реализации](https://github.com/Discake/ResearchLab/blob/chore/bootstrap/IMPLEMENTATION_PLAN.md)
- [CLI quickstart (RU)](https://github.com/Discake/ResearchLab/blob/chore/bootstrap/docs/cli-quickstart.ru.md)
- [Видение проекта](https://github.com/Discake/ResearchLab/blob/chore/bootstrap/VISION.md)

## Назначение

Проект исследует надёжный жизненный цикл локальных вычислительных задач, воспроизводимость конфигураций, работу с долговременным состоянием и безопасные границы исполнения. Это прототип инженерной платформы, не завершённый коммерческий продукт.
