# M3.7d — аварийное завершение Windows и сохранение состояния

Проверяется существующий [Job lifecycle](windows-group-lifecycle.ru.md), без изменения Domain,
схемы, смысла recovery или архитектуры рабочего исполнения. До изменений HEAD
`3825ad22ecd00b0aa83978bbe775a5da44af6feb`, ветка `chore/bootstrap`, дерево чистое;
все прежние 281 тест, включая M3.7b/M3.7c, прошли повторно.

## Что действительно делает supervisor

Тесты запускают настоящий CLI entry point отдельным процессом `dotnet`, а не заменяют
его in-process вызовом ConsoleHost. Объявленной задачей служит только синтетический
ProcessFixture. Для каждого случая уникальны события, каталоги, записи БД и tokens.
Готовность root/детей подтверждается named events и завершённым JSON manifest. Пока
подтверждённые процессы живы, supervisor открывает реальные process handles и затем ждёт
эти же handles. Отсутствие PID в списке не используется как доказательство выхода.

Авария — `TerminateProcess(cli.SafeHandle, 0xE0000037)` с ограниченным ожиданием выхода CLI.
Дерево тестовым инструментом не завершается. Supervisor и fixture-parent не получают,
не открывают и не дублируют Job handle запуска. В nested-тесте supervisor держит только
свой **внешний** Job: тот остаётся открыт с живым parent до завершения всех crash assertions
и recovery. Закрытие внешнего Job не может скрыть поломку внутреннего kill-on-close.

Проверки выхода выполняются до teardown. Аварийная уборка сигналит release-события и при
необходимости завершает только конкретные наблюдаемые тестовые process handles; она не
служит доказательством успешного crash test. Ожидания событий ограничены 20 секундами,
fixture — 45, task timeout CLI — 60. Момент аварии не определяется одним Task.Delay.

Root и дети пишут `finally` markers только при managed return/unwinding. После принудительной
гибели владельца markers отсутствуют. Если root намеренно завершён раньше, его marker
существует, дети явно подтверждены живыми и после crash CLI их markers отсутствуют.
У инструментированного CLI есть отдельный marker его finally.

## Checkpoints отсутствуют в рабочей сборке

Build IntegrationTests дополнительно собирает CLI/зависимости в отдельные `bin/CrashTests`
и копирует их в `CrashTestCli/` тестового вывода. Только эта конфигурация определяет
`RESEARCHLAB_CRASH_TESTS`. Небольшой `CrashTestCheckpoint` передаёт stage/owner PID/root PID
через приватный pipe и ждёт `continue` либо гибели процесса. Hooks защищены compile-time
условиями; в Debug/Release нет helper-типа и управляемой окружением паузы. Тест обычного
CLI проверяет отсутствие типа и передаёт те же переменные без возникновения паузы.
Пакеты и production checkpoint framework не добавлены.

Точка native-created находится после успешного CreateProcess и немедленного принятия
SafeHandles, до возврата launcher/managed acknowledgement. Тест наблюдает живой root handle
непосредственно, без объявленных потомков. Он не требует более поздней managed-ready точки,
когда launcher специально остановлен с ещё открытыми временными stdio ends. Более поздние
startup checkpoints проверяют полностью готовый root с двумя детьми. Рабочий native setup
не изменён.

## Матрица критичных случаев

Все принудительные аварии ниже завершают **только CLI**. `H` — handles всех наблюдаемых
root/детей стали signaled в пределах ожидания. `I` — явный CLI recovery даёт Interrupted,
сохраняя четыре сериализованных input snapshots, CreatedAt, сохранённые PID/StartedAt и
пустой exit code. Повторный recovery не меняет терминальные version/time/diagnostic.

| Сценарий / предусловие | Синхронизация / момент аварии | Наблюдаемый результат | Граница проверки |
| --- | --- | --- | --- |
| Долгоживущий root, Running | RootReady + durable Running; затем kill CLI | H; root finally отсутствует; Running сохраняется до I | Только специальный fixture |
| Root + один ребёнок, общие stdout/stderr | Оба готовы; записи ребёнка наблюдаются в логах | H для обоих; managed finally нет; I | Обычный CreateProcess-потомок |
| Root + два ребёнка, общие pipes | Готовы все трое; записи детей наблюдаются | H для всех; I | Два контролируемых потомка |
| Root + два ребёнка с отдельным stdio | У детей собственные redirected pipes; их записей нет в Run stdout/stderr | H для всех; I | Отдельные handles, не файловая sandbox |
| Root выходит раньше; два ребёнка держат pipes | RootExit; root handle signaled; дети подтверждены живыми; kill CLI | Дети остановлены без finally; ранний root finally существует; I | Root exit не является terminal evidence |
| Root выходит раньше; отдельное stdio детей | Те же барьеры, без наследования stdout/stderr Run | Дети остановлены; I | Не зависит от наследуемых log pipes |
| До создания Job; Created | Pipe checkpoint перед CreateJobObject | Объявленная программа не создана; manifest отсутствует; Created → I | Точная managed-точка |
| Job настроен; Created | Checkpoint после limits, до CreateProcess | Объявленная программа не создана; Created → I | Job существует без root |
| Native creation успешен; Created | Pipe сообщает реальный живой root PID; supervisor открывает handle | Root H; finally нет; сохранённый PID остаётся неизвестным; I | API вернулся; не каждая инструкция внутри него |
| До сохранения Running; Created | Checkpoint; готовые root + два ребёнка | H; PID/StartedAt не выдуманы; I | До перехода БД |
| После сохранения Running | Checkpoint после SaveRunAsync; готовая группа | H; Running → I; startup evidence сохранены | После перехода БД |
| Группа завершилась, terminal result не сохранён | Release fixtures; before-terminal; все H естественно | Durable Running → I, без восстановления Succeeded | Несохранённый успешный результат потерян |
| Succeeded уже сохранён | After-terminal; все fixture уже завершились | Succeeded остаётся Succeeded; recovery не переписывает | Настоящий успех до crash |
| Два CLI, одна БД | Оба Running; записи логов реально наблюдаются; kill первого CLI | Первый H + I; второй жив/Running и позже сохраняет Succeeded | Разные workspace/projects и output в одной БД |
| Повреждённый sidecar после настоящего crash | Группа H; повреждается только тестовый sidecar | Session unverified; Running/version прежние; восстановление evidence → I | Недоступность evidence не равна смерти владельца |
| SQLite отклоняет Running | Before-running: тестовый trigger, затем continue | CLI exit 4; вся группа остановлена; durable Created → I | Контроль storage failure, не принудительный crash |
| SQLite отклоняет terminal success | Before-terminal trigger, затем continue | CLI exit 4; durable Running → I; нет вымышленного Failed/Succeeded | Result не закоммичен |
| Сохранённый PID указывает на посторонний живой fixture | Синтетическая завершённая session намеренно хранит PID sentinel; recovery | Sentinel переживает оба recovery; записи становятся Interrupted | Контролируемый аналог stale/reused PID |
| CLI из обычного fixture-parent | Parent жив во время kill CLI | H + I, parent выживает | Ambient membership измеряется, отсутствие не предполагается |
| CLI с console attachment | Parent выделяет скрытую Win32 console; CLI её наследует | H + I, console parent жив | Настоящее console attachment; не отдельный тест Windows Terminal/IDE |
| Допустимые nested Jobs | Parent/CLI во внешнем Job; task в собственном внутреннем | Членство task во внешнем Job проверено; H + I при открытом outer Job/живом parent | Без UI restrictions и произвольных внешних limits |

Для изоляции логов fixture пишет ограниченный синтетический текст сверх FileStream buffer;
supervisor наблюдает сохранённые записи до crash. Проверяются разные tokens и output dirs.
Это **не обещание** сохранности каждого буферизованного байта: не сброшенный хвост логов
при гибели владельца может быть потерян.

## Наблюдения платформы и ограничения

Проверенная машина: Windows x64, build **26200.9711**, display version **25H2**; runtime
тестов — .NET **10.0.12**, SDK **10.0.401**. Это наблюдения данной машины, а не гарантия
всех Windows 10/11 или Server builds. Named-event/Job tests явно пропускаются вне Windows
10+. Unix и другие архитектуры здесь не проверялись.

Каждый parent-context case сохраняет синтетический evidence JSON в
`tests/ResearchLab.IntegrationTests/bin/<configuration>/net10.0/TestResults/windows-crash-context-*.json`:
OS/runtime, ambient Job membership и факт успешной CLI-only termination/recovery. В nested
case отдельно проверено членство task в контролируемом outer Job. Выделенная console
проверяет console attachment; интерактивные Windows Terminal, VS Code terminal, запуск из
Explorer и все возможные Job policies терминальных хостов напрямую не проверялись.

В этом прогоне JSON фиксирует `CliInAmbientJob=true` для ordinary, console-attached и
явного nested-контекстов. Успех ordinary/console не следует описывать как проверку предка
без Job: среда test runner уже предоставляла ambient Job.

Детерминированные паузы окружают managed/API-return/persistence boundaries. Они не могут
останавливаться на каждой инструкции внутри CreateProcessW или SQLite/OS syscall. Повторы
checkpoint не называются доказательством ненаблюдаемых micro-windows; исчерпывающая
in-syscall stress-гарантия не заявляется. Потеря питания, crash ОС, враждебное дублирование
handles тем же пользователем, broker-created процессы вне обычного Job inheritance и
произвольные ограничения ancestor Job вне проверенной матрицы. Kill-on-close — не security sandbox.

Interrupted по-прежнему означает **подтверждённую потерю владельца**, а не восстановленное
из БД доказательство остановки детей, потерянный успех или восстановленный вычислительный
результат. Process-handle наблюдения живого supervisor — отдельные свидетельства от session
proof recovery. Domain и схема не изменены. Дефектов исполнения, требующих смены архитектуры,
не найдено; промежуточные ошибки относились к синхронизации/teardown/наблюдению логов стенда.

## Воспроизведение

```powershell
dotnet build ResearchLab.slnx --no-restore
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.WindowsCrashSafetyTests
dotnet test --solution ResearchLab.slnx --no-build
dotnet format ResearchLab.slnx --no-restore --verify-no-changes
git diff --check
```

Отдельная crash matrix прошла 21/21 до итоговой усиленной полной регрессии. Локальный
runner не поддержал запрошенный `--report-trx`: в той попытке не запустилось ни одного теста.
Путь `dotnet test ... -- --help` также завершился ошибкой установленного SDK. Команды выше
работают без этих опций; reporting-зависимости для обхода не добавлялись.

Итоговая проверка 2026-10-25: **302/302 тестов solution, без пропусков**, включая все 21
усиленный crash case и прежние сценарии. Debug и инструментированная CLI сборки прошли
без предупреждений/ошибок; форматирование и diff whitespace checks успешны.

Официальные источники: [kill-on-last-close](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-createjobobjectw),
[TerminateProcess и асинхронный выход](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess),
[nested Job inheritance/limits](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs),
[создание/наследование console](https://learn.microsoft.com/en-us/windows/console/creation-of-a-console).
