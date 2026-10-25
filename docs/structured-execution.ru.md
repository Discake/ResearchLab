# M4.3 — структурированный stdout при реальном исполнении

`RunExecution` соединяет реальный `ProcessRunner` с парсером M4.1 и долговременным
приёмом M4.2. Domain, схема SQLite, доверие executable, native Windows launch,
Job containment и правила recovery не менялись. Артефакты и UI не реализованы.

## Последовательность и владение

```mermaid
sequenceDiagram
    participant C as Дочерняя программа / Windows Job
    participant R as Drains ProcessRunner
    participant L as Частные логи
    participant A as Application
    participant D as SQLite
    A->>D: Сохранить Created
    A->>R: Execute(onStarted, output sink)
    R->>C: Существующий доверенный native launch
    par Начало чтения вывода
        C->>R: Байты stdout
        R->>L: Исходные байты в пределах общего лимита
        Note over R,A: Запись метрик ожидает успешного acknowledgement
    and Подтверждение старта
        R->>A: onStarted(PID, время)
        A->>D: Run.Start + SaveRun с проверкой версии
        A-->>R: Running надёжно сохранён
    end
    loop Последовательные строки stdout с backpressure
        R->>A: UTF-8 строка + номер с единицы
        A->>A: Парсер M4.1 / ограниченный пакет M4.2
        A->>D: AddMetric и атомарный commit с проверкой версии
        A-->>R: Безопасные коды и номера строк
        R->>L: Ограниченный protocol.log
    end
    R->>A: EOF stdout / завершение сбора
    A->>D: Финальный flush принятых полных событий
    C-->>R: Root exit И пустой Windows Job
    Note over R,L: Оба drains и записи завершены; фоновых collector-задач нет
    R-->>A: ProcessResult
    A->>D: Terminal через текущую версию ingestion
```

Чтение raw stdout/stderr начинается рядом с `onStarted`, как и раньше. Один барьер
подтверждения запрещает приём типизированной метрики до сохранения Running. Чистый
парсер M4.1, обычные строки логов и диагностика парсера работают до acknowledgement.
Только разобранная метрика ожидает барьер внутри Application, удерживая ограниченную
строку/read buffer; ребёнок может остановиться на ограниченном pipe ОС. Обычные логи
продолжают читаться, включая startup crash checkpoints M3. Stderr читается независимо. Application acknowledgement не должен
ожидать завершения ребёнка или stdout: это противоречит backpressure. Неуспешное
подтверждение освобождает raw-drain барьер с запретом метрик, подтверждает cleanup и
пробрасывает исходную ошибку. Метрики в Created не записываются.

В `IProcessExecution` добавлена перегрузка с sink. Настоящий ProcessRunner её реализует;
адаптеры без вывода сохраняют прежнюю перегрузку. Декоратор потокового адаптера обязан
передать sink и сохранить контракт acknowledgement/завершённого сбора.
`RunMetricCollector` владеет разбором, ограниченными номерами ожидающих строк, ingestion
и сохранением ошибки callback. Исполнитель не получает Run, не выбирает Run status
и не обращается к DbContext.

Ingestion создаётся лениво на первой разобранной метрике после acknowledgement.
Затем он владеет сохранением метрик/terminal через ту же проверку версии Run. Исходный
экземпляр Run после метрик не сохраняется с устаревшей версией. Без метрик сохраняется
терминальный путь M3. RunOperation удерживает владение при ProcessCleanupException;
повтор продолжает тот же процесс/сбор, не запускает программу заново.

## Байты, ограничения и диагностика

- Исходные байты записываются в stdout.log/stderr.log до структурированной обработки.
  Нет декодирования/перекодирования, удаления событий, BOM или преобразования newlines.
- Только stdout разбивается по LF. Поддержаны CRLF и последняя строка без перевода
  строки. Framer удерживает максимум 8195 байт: лимит 8 KiB плюс распознавание
  терминатора/переполнения. Остаток слишком длинной строки отбрасывается до LF.
  Парсер диагностирует слишком длинное protocol-событие один раз; длинный обычный
  текст остаётся обычным raw-логом.
- Read buffers: 8192 байта на поток. M4.2 удерживает максимум 128 событий; сопоставление
  строк — максимум 128 номеров. Нет неограниченной истории/кэша, channel, фонового
  writer, scheduler или отсоединённой задачи сбора.
- Передача stdout ожидает Application и создаёт backpressure; stderr читается отдельно.
  Flush выполняется по размеру, прошедшему интервалу на следующей метрике и при
  завершении stdout. Таймер flush во время отсутствия событий не добавлялся.
- Существующий **общий лимит stdout + stderr 1 MiB** сохранён. Разбираются только
  удержанные байты. При cutoff/ошибке чтения неполный хвост не превращается в событие;
  уже принятые полные события сохраняются.
- Дополнительный частный `protocol.log` содержит JSON Lines `{line, code}`: максимум
  256 записей об ошибках и один маркер `DiagnosticsTruncated`. Код ограничен 64
  символами; текущие коды — фиксированные ASCII-идентификаторы парсера/домена.
  Payload, имя метрики, единица, текст исключения, секреты и абсолютные пути не
  интерполируются. Порядок диагностики соответствует обнаружению: отказ отложенного
  пакета может следовать за ошибкой парсера более поздней строки; исходный номер
  строки всё равно сохранён.
- Чтение доступно через `RunLogs.OpenReadAsync(id, RunLogStream.Protocol)` либо файл
  `<data-dir>/<RunId:N>/protocol.log`. Пути stdout/stderr и CLI JSON совместимы.
  Старые попытки/адаптеры без вывода могут не иметь protocol-файла. Частные raw-логи
  могут содержать секреты ребёнка; диагностика их не дублирует.

Некорректные/неподдержанные события и несовместимые единицы — диагностируемые ошибки
данных, а не автоматический Failed. Корректные соседи сохраняют Sequence и наблюдения.
Известный набор кодов ограничивает диагностику независимо от raw log quota.

## Обязательный сбор и ошибки

Финальный сбор stdout вызывает FlushAsync M4.2 и записывает диагностику отказов.
Только после завершения root/Job, обоих drains и всех записей Application сохраняет
Succeeded/Failed/Canceled/TimedOut. Root exit сам по себе не завершает Windows Run.
Timeout и cancellation используют прежнего владельца и остановку Job. Принятые
метрики сохраняются после nonzero exit, timeout и cancellation.

Ошибка raw/diagnostic collection не допускает Succeeded. При отказе записи диагностики
могут сохраниться наблюдения, принятые до остановки сбора, а после cleanup записывается
Failed. Ошибки БД/optimistic concurrency пробрасываются workflow исходным исключением,
даже если process adapter обернул либо скрыл ошибку callback. Успех не записывается;
в БД может остаться Running для явного recovery. Прежние commit сохраняются. Отказ
terminal persistence также пробрасывается и не меняет сохранённые наблюдения.
Автоматического повторного запуска программы нет.

Существующий пятисекундный лимит collection cleanup после завершения группы сохранён.
Медленная запись БД может выйти за него: владение и I/O удерживаются через
ProcessCleanupException до фактического завершения сбора. Пока запись идёт, terminal
не сохраняется. Backpressure влияет на время работы ребёнка и достижение timeout.
Авария может потерять текущий несохранённый пакет; exact-once replay после аварии и
idle flush scheduler не реализованы. Unix сохраняет прежнюю root/best-effort-tree
модель и не проверен заново как эквивалент Windows containment.

## Изменённые области и проверки

Application: ProcessContracts, RunExecution, новый RunMetricCollector, enum RunLogs.
Execution: ProcessRunner, RunLogFiles, новый StructuredStdout.
Тесты: новые StructuredExecutionTests и MetricFixture, маршрутизация fixture Program.
Документация: этот EN/RU-контракт, статус интеграции M4.2 и README.
Domain, infrastructure/schema, CLI, native Windows/Job и recovery не менялись.

Тесты используют настоящие дочерние native fixture-процессы и временную SQLite;
данные синтетические: точные raw-байты, побайтовые записи UTF-8/CRLF, последняя строка
без LF, параллельный stderr, invalid/unknown/unit/oversized события, ограниченная
диагностика, 2000 упорядоченных точек, raw quota, nonzero exit, timeout/cancel,
вывод до Running, отказы SQLite Running/MetricPoint/terminal, скрытая ошибка callback,
отказ diagnostic-записи и отдельные процессы CLI start/reopen. Полная регрессия
включает прежние Windows cancellation, group lifecycle, native-launch и crash tests.

## Проверки — 2026-10-25

Финальная проверка в текущем Windows workspace:

```text
dotnet build ResearchLab.slnx --no-restore
dotnet test --solution ResearchLab.slnx --no-build --no-restore
dotnet format ResearchLab.slnx --verify-no-changes --no-restore
dotnet ef migrations has-pending-model-changes --project src/ResearchLab.Infrastructure --no-build
git diff --check
```

Все команды успешны. Сборка без предупреждений/ошибок. Полный набор: **412 успешно,
без ошибок и пропусков**, включая **23 новых сценария structured execution** и прежние
Windows crash/cancellation/native/group lifecycle tests. Время: 1 мин 22 с. EF:
изменений модели нет. Domain/schema/CLI/native launcher/recovery не менялись.
Зависимости и миграции не добавлялись. Коммит и push не выполнялись. Unix не запускался.

Наблюдаемые результаты синтетических fixture-сценариев:

| Сценарий | Сохранённый результат |
| --- | --- |
| Два корректных наблюдения | Succeeded, значения 0.5/0.2, Sequence 1/2, Version 4 |
| Invalid/unknown/несовместимая единица между точками | Succeeded, те же два значения/sequence, безопасный код для raw-строки 3 |
| Nonzero exit | Failed, exit 7, обе метрики сохранены, Version 4 |
| Timeout / явная отмена | TimedOut / Canceled, все 130 выведенных точек сохранены, Version 132 |
| SQLite trigger отклонил INSERT 129 | Исходный DbUpdateException, остался Running, первые 128 commit сохранены |
| Отказ UPDATE Running | Исходный DbUpdateException после подтверждённого выхода процесса, Created без метрик |
| Отказ terminal UPDATE | Исходный DbUpdateException, Running с обеими сохранёнными точками |
| Отдельные CLI start и CLI show | Тот же Run ID и MetricPoint ID после повторного открытия |

Предварительная регрессия выявила слишком ранний барьер в stdout drain: обычный
вывод блокировался на Windows crash checkpoints до Running (408/411 успешно).
Финальный барьер находится на приёме типизированной метрики в Application; обычные
логи продолжают читаться. Новый тест с 64 KiB обычного stdout до Running защищает этот
сценарий; весь набор повторно прошёл. Первый fixture-сигнал готовности после pipe flush
перенесён к одному write без flush, чтобы не зависеть от acknowledgement. Целевые
прогоны прошли путь от 18/19 к 19/19, затем 22/22 и финальному полному прогону с 23
новыми сценариями. Предварительный diff check нашёл лишние пустые строки EOF — удалены.
Throughput benchmark и принудительная авария во время metric commit не выполнялись;
существующие lifecycle crash-тесты запускались.
