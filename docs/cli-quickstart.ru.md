# ResearchLab — быстрый старт CLI и проверка recovery (Windows)

Короткое практическое руководство для M3. Подробные контракты: [foreground-запуск](foreground-run.ru.md), [recovery](run-recovery.ru.md), [M3.6](local-run-completion.ru.md), [итоговые проверки M3](m3-final-verification.ru.md).

**Проверенная среда:** Windows 11 x64, .NET 10, PowerShell 7.3+ (предпочтительно). Выполняйте команды **из корня репозитория в ветке `chore/bootstrap`**. Запускать тестовые команды можно в своём терминале. Для `run start` требуется вручную посмотреть сведения о программе и ввести `yes`; это подтверждает доверие только в данном вызове.

## 1. Собрать проект и подготовить демонстрацию

```powershell
dotnet restore ResearchLab.slnx
dotnet build ResearchLab.slnx --no-restore

$PSNativeCommandArgumentPassing = 'Standard'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$demo = & ./samples/foreground-demo/prepare-demo.ps1 | ConvertFrom-Json
$common = @('--database', $demo.database, '--data-dir', $demo.logs)
$demo | Format-List
```

Скрипт создаёт **новый временный** workspace, инициализирует SQLite, импортирует Project и создаёт Experiment. Он **не запускает исследовательскую программу**. Запоминайте `$demo` и `$common` в этой PowerShell-сессии; в новом окне переменные отсутствуют. `$demo.logs` — приватный каталог данных и логов, а не проектный workspace.

Windows PowerShell 5.1 имеет отличающиеся правила передачи JSON-аргументов в native программы. Для команд ниже используйте PowerShell 7.3+; в 5.1 JSON со строками и пробелами может передаваться неправильно, даже если текст команды выглядит корректно.

## 2. Успешный запуск

```powershell
$run = & dotnet $demo.cli @common run start $demo.experimentId --parameters '{"iterations":4,"scale":2,"label":"first","verbose":true}' | ConvertFrom-Json
$run | ConvertTo-Json -Depth 8
```

После проверки показанных executable/fingerprint/параметров введите **`yes`** и Enter.

Ожидается `status: Succeeded`, `exitCode: 0`, а в `logOutput.stdout` — строка `Sum: 20`. Вывод демонстрационный, **не научный benchmark**. Поля Run и пути логов берутся из реального результата, не подставляйте заранее вымышленные идентификаторы.

## 3. История и сохранённые логи

```powershell
& dotnet $demo.cli @common run show $run.id --include-logs
& dotnet $demo.cli @common run list --experiment $demo.experimentId
```

Эти команды заново читают сохранённый Run из SQLite. Логи лежат в `<data-dir>/<RunId без дефисов>/stdout.log` и `stderr.log`. Каталог `output` рядом с ними предназначен для результатов программы; сама демонстрационная программа не обязана создавать в нём артефакты.

## 4. Преднамеренная ошибка

```powershell
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"fail":true}'
$LASTEXITCODE
```

Снова проверьте review и введите `yes`. Ожидается `status: Failed` и **`exitCode: 7` внутри JSON Run**. Код завершения **самого CLI** — **6**, поскольку он сообщает о Failed Run. Это два разных кода. При этом stdout/stderr сохраняются.

## 5. Отмена через Ctrl+C

```powershell
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"delayMilliseconds":30000}'
```

После `yes`, пока Run исполняется, нажмите **Ctrl+C один раз**. CLI должен запросить отмену, дождаться подтверждения остановки и сохранить `Canceled` (код CLI 130, exit code Run — `null`). Не используйте для этого `Stop-Process`: принудительная смерть CLI — другой сценарий, проверяемый отдельно.

## 6. Проверка timeout (необязательная)

По умолчанию в демо timeout равен 60 секундам. Для быстрого воспроизведения **только во временном workspace** измените его на 1 секунду:

```powershell
$configPath = Join-Path $demo.workspace 'researchlab.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.tasks.benchmark.timeoutSeconds = 1
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))

& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"delayMilliseconds":30000}'
$LASTEXITCODE
```

После просмотра изменившегося fingerprint подтвердите `yes`. Ожидается `TimedOut`, код CLI **124**, Run exit code — `null`. Для следующих обычных сценариев лучше вызвать `prepare-demo.ps1` заново: он создаст чистый каталог и восстановит исходный timeout.

## 7. Recovery: согласовать состояние после аварии

```powershell
$recovery = & dotnet $demo.cli @common run recover | ConvertFrom-Json
$recovery | ConvertTo-Json -Depth 6
```

CLI не выполняет recovery автоматически. Команда анализирует сохранённые файловые свидетельства владения и возвращает:

| Поле | Значение |
| --- | --- |
| `recovered` | Run, для которых подтверждена потеря владельца и записан статус `Interrupted` |
| `conflicts` | Run с конфликтом конкурентного обновления, требующие отдельной повторной проверки |
| `unverifiedSessions` | Сессии с активным владельцем или недостаточными/занятыми свидетельствами — **не изменяются** |

**После обычных Succeeded/Failed/Canceled/TimedOut все три массива, скорее всего, пусты — это правильный результат.** Код CLI 0 означает завершение операции проверки, **не** отсутствие конфликтов: всегда смотрите содержимое массивов.

Повторный вызов должен быть безопасен:

```powershell
& dotnet $demo.cli @common run recover
```

Если Run восстановлен, его можно открыть через `run show <run-id>`. `Interrupted` означает потерю подтверждённого владельца, а не восстановленный результат вычисления. Recovery **не** перезапускает программу и **не** завершает процесс по сохранённому PID.

### Как безопасно проверить настоящий crash/recovery

Обычный `Ctrl+C` создаёт `Canceled`, а не `Interrupted`. Не стоит специально убивать произвольный `dotnet.exe` ради теста. В репозитории уже есть отдельные изолированные Windows crash-тесты, которые принудительно завершают **только созданный тестом CLI**, проверяют прекращение принадлежащих ему процессов и последующий recovery:

```powershell
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.WindowsCrashSafetyTests
```

Это проверяет crash containment без риска завершить чужой рабочий процесс. Не считайте один пустой результат `run recover` доказательством работы crash recovery: это разные сценарии.

## 8. Когда M3 можно считать проверенным

Обычный CLI сценарий проверяет Succeeded/0, Failed/7, раздельные логи, историю, Ctrl+C, timeout и безопасный повтор recovery. Автоматические Windows crash tests отдельно проверяют принудительную гибель владельца и `Interrupted`. Неизвестные версии Windows и Linux/macOS остаются вне подтверждённой области. Job Objects управляют группой процессов, но **не являются изоляцией от файловой системы, сети или внешних сервисов**.

Следующий этап — **M4: структурированные метрики и регистрация артефактов**.
