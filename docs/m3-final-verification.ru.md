# M3.7e — окончательная проверка (2026-10-25)

M3.7b/c/d присутствовали в `chore/bootstrap`; перед проверкой рабочее дерево было чистым.
Рабочий код, Domain-контракт, миграции и зависимости не изменялись.

## Выполненные проверки

| Команда | Фактический результат |
| --- | --- |
| `dotnet restore ResearchLab.slnx` | Успешно |
| `dotnet build ResearchLab.slnx --no-restore` | Успешно, 0 предупреждений/ошибок; включая отдельные CrashTests binaries |
| `dotnet test --solution ResearchLab.slnx --no-build --no-restore` | 302/302, 0 пропусков; повтор вне sandbox дал тот же результат |
| `dotnet format ResearchLab.slnx --verify-no-changes --no-restore` | Прошёл в начале и после документации вне sandbox; промежуточный повтор в sandbox упал при загрузке MSBuildWorkspace |
| `dotnet ef migrations has-pending-model-changes --project src/ResearchLab.Infrastructure` | Успешно, изменений модели нет |
| `git diff --check` | Прошёл после изменения документации |

Полный набор включает 21 [Windows crash case](windows-crash-safety.ru.md), импорт,
валидацию параметров, trust/revalidation, отдельное и конкурентное хранение Run,
stdout/stderr, историю, SQLite, cancellation/recovery и доменные инварианты терминальности
и снимков. Fixtures синтетические, это не научные результаты. `dotnet --info` частично
вывел версии, но завершился кодом 1 в sandbox из-за запрета Service Control Manager.

## Foreground-demo

Настоящий prepare-demo создал хранилище, импортировал проект и создал Experiment.
Вне sandbox явное `yes` через stdin разрешало каждый показанный review: успешный запуск
сохранил Succeeded/0 и `Sum: 20`; `fail=true` — Failed/7 (CLI exit 6).
Повторное чтение логов/истории прошло; отдельные каталоги Run содержали `output`.
После изменения только временной demo-конфигурации на timeout 1 секунда задержка
30 секунд дала TimedOut/null exit code (CLI exit 124). Явный `run recover` завершился
с пустыми списками для этих уже терминальных Run.

Ctrl+C проверен прошедшим тестом настоящего console-control-event, включая
Program/CancelKeyPress и сохранённый Canceled, а не ручным нажатием в видимом терминале.
Recovery после гибели владельца проверен crash-матрицей, а не отдельной ручной аварией.
Полностью ручное выполнение всех девяти шагов не заявляется. Выделение output наблюдалось;
сама программа суммы не записывает artifact.

Первый demo использовал sandbox-temp и сохранил Failed до старта, без PID/exit code.
Повтор с обычным temp вне sandbox прошёл. Какое именно ограничение sandbox мешало старту,
не установлено; это не доказательство дефекта production crash containment. Критические
crash tests прошли в обоих полных запусках.

Подготовка и success/log round trip также прошли в PowerShell 7.6.5 и Windows
PowerShell 5.1.26100.9711 с явной UTF-8 кодировкой console. В 5.1 кавычки JSON требовали
legacy escaping; проверенное значение `shell-check` не содержало пробелов. Две попытки
5.1 со значением `shell check` дали ошибку CLI parsing. Для произвольного JSON предпочтителен
PowerShell 7.3+ с Standard argument passing; общая совместимость аргументов 5.1 не заявляется.
Начальная не-ASCII метка demo дала replacement characters в логах при стандартных console
encodings; настройка shell не исправляет уже выданные дочерней программой байты.
Логи сохраняют raw bytes, чтение использует UTF-8 с заменой некорректных байтов.

## Платформа и границы гарантий

Здесь проверена Windows 11 25H2 x64, build 26200.9711, SDK 10.0.401, runtime 10.0.12.
Предполагаемая совместимость — Windows 10+ x64 с нужными creation-time Job attributes и
совместимыми ancestor Jobs. Другие Windows 10/11 builds, Windows Server, ARM64/x86,
политики запуска Windows Terminal/IDE и ограничивающие ancestor Jobs не проверены.
Linux/macOS execution и locking не запускались.

Windows создаёт приватный ненаследуемый kill-on-close Job и передаёт его CreateProcessW
при создании процесса с явным списком стандартных handles. Ошибки создания/настройки Job,
attributes или процесса завершают startup; fallback без containment запрещён, иначе
теряется гарантия владения. Несовместимый ambient Job поэтому может отклонить задачу.

Run ждёт root exit/code, нулевого числа активных процессов Job и завершения collection:
потомки могут пережить корень и независимо закрыть или удерживать pipes. Нулевой exit code
корня не доказывает успех каждого потомка; их exit codes не собираются. Succeeded описывает
контракт исполнения/collection, а не научную корректность.

Авария CLI закрывает его приватный Job handle; при закрытии последнего handle ОС завершает
обычных contained-потомков. Crash-матрица наблюдает реальные process handles, но не каждую
инструкцию внутри OS/DB calls, сохранность при потере питания или buffered tails логов.
Несохранённый terminal outcome остаётся Created/Running до явного recovery. Interrupted
фиксирует подтверждённую потерю владельца, не восстановленный результат и не доказанное
через БД завершение потомков. Recovery никогда не сигналит сохранённые PID.

На Linux эквивалента Job в реализации нет: root exit и best-effort
`.NET Kill(entireProcessTree: true)` не доказывают очистку потомков после root exit или
гибели CLI. Ни один backend не является security sandbox. Job не ограничивает доступ к
файлам/сети, credentials и внешним сервисам. Работа через broker/service вне обычного
Job inheritance и враждебное дублирование handles вне гарантии. Завершение локальных
процессов не откатывает внешние запросы и не останавливает удалённые вычисления.

Приватное локальное хранилище и соблюдение ownership-протокола остаются предпосылками.
Проверки links/reparse points не устраняют TOCTOU, враждебную запись тем же пользователем,
storage aliases и подмену sidecar. Output allocation не даёт валидацию artifacts, quotas
или immutable evidence. См. [ProcessRunner](minimal-execution.ru.md),
[cancellation](managed-cancellation.ru.md), [recovery](run-recovery.ru.md),
[M3.6](local-run-completion.ru.md).

## Завершённость и следующий этап

Критерии локального исполнения M3 закрыты в указанной области Windows: доверенный
foreground launch, отдельные попытки, сохранённые входы/статусы/история, ограниченные
раздельные логи, диагностика ошибок, cancellation/timeout, явный ownership recovery и
проверенный Job crash containment. Критических crash-дефектов не обнаружено; результаты
не обосновывают отдельное задание на масштабное переписывание исполнения.

Можно начинать M4. Весь MVP ещё не завершён: protocol ingestion, зарегистрированные
immutable artifacts, сравнение метрик, UI и Research Story export относятся к следующим
этапам. Ручной Ctrl+C в видимых терминалах, другие ОС и общий JSON quoting 5.1 остаются
пробелами проверки, а не молча засчитанными результатами.
