# Foreground-запуск эксперимента через CLI

Существующий `researchlab` теперь связывает ProjectCatalog, RunExecution/ProcessRunner,
RunHistory и RunLogs. Нет дополнительного host, daemon, очереди или detached-driver.
Пример воспроизводится из свежего checkout на настоящей консольной программе; писать
C# host-код или вручную менять SQLite не требуется. Проверена Windows/PowerShell 7.

## От пустого каталога до сохранённого Run

Из корня репозитория:

```powershell
dotnet restore ResearchLab.slnx
dotnet build ResearchLab.slnx --no-restore
$demo = & ./samples/foreground-demo/prepare-demo.ps1 | ConvertFrom-Json
$common = @('--database', $demo.database, '--data-dir', $demo.logs)
$demo | Format-List
$run = & dotnet $demo.cli @common run start $demo.experimentId --parameters '{"iterations":4,"scale":2,"label":"first","verbose":true}' | ConvertFrom-Json
```

Прочитайте review в stderr: абсолютный путь executable, буквальные аргументы, timeout,
fingerprint и проверенные параметры с defaults. Введите **yes** и Enter для разрешения
только текущего вызова. Любой другой ответ или EOF отклоняет запуск без создания Run.
Опции `--yes` и постоянного доверия нет. Подмена executable/конфигурации между review,
подтверждением и стартом отклоняется существующими проверками Application. Доверие
покрывает объявленную точку входа/конфигурацию, а не все зависимости или песочницу ОС.

Команда ждёт исполнения и сохранения, затем возвращает JSON. Пример действительно
вычисляет масштабированную сумму 1+2+3+4: при scale 2 stdout содержит `Sum: 20`.
Это демонстрация локального исполнения, а не benchmark или записанные научные метрики.

```powershell
$run | ConvertTo-Json -Depth 8
# Другой вызов CLI читает тот же сохранённый Run и захваченные логи:
& dotnet $demo.cli @common run show $run.id --include-logs
& dotnet $demo.cli @common run list --experiment $demo.experimentId
```

Ответ содержит Run ID, статус, записанные параметры/времена, exit code, безопасную
диагностику Application, относительные пути логов и `logOutput.stdout`/`stderr`. Текст
логов декодируется UTF-8 с заменой неверных байтов и экранируется JSON, включая ANSI/
управляющие символы. Захват сохраняет общий лимит 1 MiB; показ CLI дополнительно ограничен
1 MiB символов на поток, с `logOutput.truncated` при увеличении приватных файлов после
захвата. `run show` без `--include-logs` сохраняет лёгкий ответ с метаданными/путями.
Логи/параметры могут содержать приватные локальные данные: это не публичный экспорт.
Логи показываются после завершения; live tailing не добавлен.

Скрипт подготовки создаёт новый временный каталог, копирует собранный пример, записывает
`researchlab.json`, затем использует только команды CLI:

```text
database init
project import <workspace> --name <name>
experiment create --project <project-id> --definition <experiment-json-file>
```

Он возвращает фактические пути и ID как JSON, не запускает задачу и не подтверждает
доверие. `-Directory <новый-каталог>` задаёт другое расположение; существующие каталоги
отклоняются без перезаписи. Приватные БД/логи должны находиться вне импортированного
workspace. У задачи примера установлен timeout 60 секунд.

Для своей программы объявите native executable, буквальные args и timeout в
`researchlab.json`, импортируйте workspace и задайте определение Experiment ниже.
На Windows существующий runner принимает точки входа `.exe`, без shell-fallback для
`.cmd`/`.bat`. На Unix нужны имя без `.exe` и права исполнения; Unix здесь не проверен.
Скрипт подготовки автоматически выбирает собранную точку входа своей платформы.

## Параметры и подготовка метаданных

`--parameters` — один JSON-объект в одном аргументе оболочки. Преобразование использует
сохранённую схему Experiment: int32, конечный double, JSON bool или string. JSON-число
для объявленного double преобразуется в double и при записи `2`. Строки не превращаются
в числа/bool. Null, массивы/объекты в значениях, повторные/неизвестные имена, отсутствие
обязательного параметра, неверный тип, запрещённые значения и выход за границы дают ошибку
до подтверждения доверия и вставки Run. Defaults/ограничения принадлежат Domain. В ребёнок
параметры передаются через `RESEARCHLAB_PARAMETERS_JSON`, без подстановок в аргументы
задачи или shell-команду.

`experiment create --definition` читает JSON-файл с прямым отображением существующей
модели Experiment; [полный пример](../samples/foreground-demo/experiment.json):

```json
{
  "name": "My experiment",
  "description": "Local execution example",
  "taskKey": "benchmark",
  "parameters": [
    { "name": "n", "type": "int", "required": true, "default": 10, "minimum": 1, "maximum": 100 },
    { "name": "mode", "type": "string", "default": "fast", "allowedValues": ["fast", "precise"] }
  ]
}
```

`parameters` можно опустить для эксперимента без параметров. Поля записи: `name`, `type`
(int/double/bool/string), `required`, `default`, `minimum`, `maximum`, `allowedValues`.
Неизвестные поля определения отклоняются. `project import` и `experiment create` вызывают
ProjectCatalog; они не исполняют задачи и не создают доверие. Только явный `database init`
создаёт/мигрирует SQLite. Остальным командам нужна существующая совместимая БД; они не
инициализируют/мигрируют её скрыто и не вызывают recovery. Команды чтения по-прежнему
открывают Mode=ReadOnly, запись — существующую БД Mode=ReadWrite. Все команды требуют
явные `--database` и `--data-dir`, до или после команды.

## Ctrl+C, ошибки и владение

```powershell
# После review и yes нажмите Ctrl+C, пока пример ожидает:
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"delayMilliseconds":30000}'
# Настоящий ненулевой выход ребёнка с сохранением stdout/stderr и exit code 7:
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"fail":true}'
```

Каждый start снова спрашивает доверие, включая эти примеры. После сохранения Created CLI
удерживает RunOperation и foreground-driver. Обработчик Ctrl+C в Program подавляет
автоматическое завершение и превращает токен запроса в `RequestCancellation()` этой
операции. CLI ждёт, не отменяя исполнение/сохранение статуса, и выводит записанный итог.
Повторный Ctrl+C не завершает хост небезопасно. Поздний Ctrl+C не переписывает выбранный
естественный выход или timeout. У Canceled/TimedOut доменный exit code остаётся null.
До получения владения Ctrl+C отменяет подготовку/интерактивное подтверждение.

Каждое ожидание очистки Execution сохраняет прежнее ограничение. Если остановка/сбор
не подтверждены, CLI остаётся в foreground с тем же владельцем и повторяет **очистку**
через секунду, не запуская новый процесс. Сообщается, что терминальный итог не заявлен;
постоянный запрет ОС может удерживать команду до выхода процесса или успешной очистки.
Фонового worker повторов нет. Ошибка терминальной записи БД возвращается как ошибка
хранилища с ранее показанным Run ID, без ложного сохранённого итога. Принудительное
завершение хоста, закрытие консоли или всей группы процессов может обойти Ctrl+C-handler;
[восстановление M3.3](run-recovery.ru.md) остаётся явным и фиксирует потерю владельца,
а не гарантированную остановку осиротевших потомков.

| Код | Значение |
| --- | --- |
| 0 | Успешная команда / Succeeded Run |
| 1 | Непредвиденная ошибка операции |
| 2 | Ошибка использования или проверки входных данных/домена |
| 3 | Запрошенный Project/Experiment/Run отсутствует |
| 4 | Ошибка хранилища, доступа, конфигурации или сохранения |
| 5 | Доверие отклонено, отсутствует или изменилось |
| 6 | Сохранённый Failed Run; exit code ребёнка отдельно указан в JSON |
| 124 | Сохранённый TimedOut Run |
| 130 | Сохранённый Canceled Run либо отмена до владения |

Интеграционные тесты запускают пример настоящим CLI из пустого SQLite, читают его другим
вызовом, отклоняют EOF/отказ от доверия, повторно проверяют изменённые задачу/executable,
валидируют типизированный ввод, различают ненулевой выход/timeout/отмену, проверяют
удержание владельца и терминальную запись. Windows-тест console control event проходит
настоящий путь Program/CancelKeyPress в отдельной скрытой **тестовой** консоли.
