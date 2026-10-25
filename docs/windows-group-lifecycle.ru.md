# M3.7c — жизненный цикл Windows Job-группы

На Windows рабочий `ProcessRunner` и foreground CLI используют [native launcher](windows-native-launcher.ru.md).
Неподдерживаемое или ненадёжное Job containment даёт ошибку старта без fallback на
`Process.Start`. Требуется Windows 10+; ограничения вложенных Job окружения могут отклонить
старт. Переходы Domain, Application lifecycle, RunRecovery и схема SQLite не изменены.
Комментарий `IProcessExecution` теперь описывает границу исполнения, а не только корень.

## Ответственность и свидетельства завершения

`OwnedProcess` сохраняет общую подготовку запуска/хранилища, арбитраж, таймер, дренирование,
общий лимит 1 MiB, обработку acknowledgement и повтор очистки. `IRunProcess` — небольшой
внутренний адаптер ресурсов. `WindowsRunProcess` владеет native process/Job и сохраняет
exit evidence корня; `WindowsJobControl` оборачивает два Job API. `LocalRunProcess`
сохраняет прежний non-Windows backend с выходом корня и best-effort остановкой дерева.
Эквивалентные групповые гарантии не обещаются; Unix здесь не проверен.

Естественное завершение Windows требует signaled root handle, сохранённого exit code,
`QueryInformationJobObject(JobObjectBasicAccountingInformation).ActiveProcesses == 0`
и завершившихся stdout/stderr drains. Для Succeeded дополнительно нужны root exit zero и
отсутствие ошибок сбора/наблюдения группы. Exit codes потомков не собираются и не заявляются.
EOF, выход корня, успешный TerminateJobObject и закрытие Job не заменяют нулевой счётчик.
Сигнал Job handle и пустой список PID не используются как барьер.

Арбитраж сериализован одной блокировкой попытки. Подтверждённо завершённая группа выигрывает
поздние cancel/deadline; завершившийся корень с живыми потомками — нет. Timeout действует
до завершения группы. Cancel/deadline/критические ошибки вывода запрашивают
`TerminateJobObject`, в том числе после выхода корня. Первая выбранная причина сохраняется;
критическая ошибка сбора или query запрещает успех/Canceled/TimedOut и даёт Failed только
после подтверждённой очистки. Подтверждение старта по-прежнему завершается до возврата
результата или обычного исключения.

## Порядок освобождения native references

Launcher выделяет владельца до CreateProcess и закрывает primary thread handle сразу после
принятия успешного старта. Дочерние ends каналов, attribute values/list, environment block
и stdin writer освобождаются при возврате из запуска. Windows-адаптер также выделяется
до запуска: после создания процесса нет потенциально ошибочного выделения обёртки.

Под блокировкой runner адаптер опрашивает корневой handle без фоновой OS wait registration.
После signaled-состояния он читает exit code и **закрывает свой root process handle**.
Лишь затем опрашивается Job accounting. Runner не удерживает managed Process, дубликат
process handle, thread handle или регистрацию ожидания корня во время ожидания нуля.
Job и потоки остаются во владении; они не являются ссылками на root process.

Microsoft описывает уменьшение accounting после выхода процесса и освобождения ссылок.
Ссылки на процесс/поток у других владельцев или завершение ресурсов ОС могут консервативно
задержать доказательство нуля. Тесты включают настоящий accounting probe при удерживаемом
root handle и после его освобождения, а также запуски, завершающиеся без взаимной блокировки
на собственных ссылках runner. Счётчик до освобождения — диагностическое наблюдение,
а не переносимое утверждение об одинаковом поведении всех версий Windows.

После подтверждения нуля должны завершиться оба drains; затем закрываются логи, Job и
оставшиеся потоки/ресурсы. Закрытие уже пустого Job — освобождение ресурса, а не доказательство
завершения. Логи не освобождаются при незавершённом drain.

## Ошибки и повтор

Ошибка query сохраняется как основание против Succeeded. Driver пытается остановить группу
и подтвердить завершение; неизвестный счётчик нельзя считать нулём. Ошибка TerminateJobObject
может сопровождаться независимым свидетельством естественного завершения, но сам успех
вызова ничего не подтверждает. Принудительная очистка ограничивает ожидание подтверждения
пятью секундами, затем отдельно ограничивает drains пятью секундами. Естественное исполнение
ограничено timeout задачи. Границы относятся к асинхронным ожиданиям, а не длительности
syscall, закрытия файлов или callbacks БД Application.

Отказ остановки живой группы, недоступный accounting query, задержка освобождения ссылок,
ошибка наблюдения root exit или незавершённый I/O дают `ProcessCleanupException`.
Тот же владелец сохраняет Job, exit evidence, логи, drains, выбранную причину и исходную
ошибку acknowledgement. Терминальной записи нет. Повтор выполняет только cleanup/сбор;
`attempted` запрещает повторный запуск. Timeout drains отменяет чтения и закрывает read pipes;
повтор всё равно требует завершившихся drains и сообщает ошибку сбора.

Преждевременная потеря/закрытие настоящего Job handle **не исправляется по сохранённым PID**.
Kill-on-close может инициировать остановку ОС, но runner потерял accounting proof и продолжает
сообщать неподтверждённую очистку. Выход корня и EOF не подменяют доказательство. Отдельный
fixture-процесс проверяет настоящее закрытие Job и повторное сохранение owner; выход этого
тестового владельца освобождает оставшиеся ресурсы без выдуманного терминального Run.
Рабочий владелец обязан держать Job приватным и открытым до подтверждения. Внешний участник
с правами дублирования handles того же пользователя находится вне этой границы доверия.

Ошибка сохранения Running/startup acknowledgement останавливает всю группу и завершает сбор
до передачи исходной ошибки. Если cleanup также не удался, сначала возвращается исключение
с владельцем; успешный повтор передаёт исходную SQLite/callback ошибку. Ошибка терминального
сохранения передаётся после подтверждённой очистки и не превращается в вымышленный Failed
или повторный запуск. Контракты логов, параметров и output directory сохранены.

Тестовый seam `IProcessTerminator.Stop(Action stopGroup)` теперь оборачивает backend stop,
а не принимает managed Process: тот не представляет Job после освобождения root handle.
Тесты всё ещё запрещают или игнорируют остановку; runner независимо проверяет завершение.
Внутренний Job-control seam провоцирует настоящие Win32 invalid-buffer/invalid-handle
ошибки; log-writer seam — настоящую ошибку записи закрытого FileStream. Рабочий путь
не меняется этими seams; Win32-зависимостей в Domain/Application нет.

## Проверки и готовность к crash tests

Реальные Windows-тесты покрывают живого потомка с унаследованными и закрытыми каналами,
позднее завершение потомка, позднюю отмену, timeout после root exit, ошибки query/terminate,
cleanup retry без повторного старта, настоящее преждевременное закрытие Job, конкурентные
Run, ошибку записи логов, точный лимит/переполнение, Unicode/JSON/аргументы/CWD/output directory
и startup/terminal ошибки SQLite. Прежние CLI-тесты включают успех, `fail=true` с кодом 7
и Ctrl+C. Ожидания готовности/выхода ограничены; управляются только тестовые процессы.
Прежнее root-only ожидание поздней отмены теперь учитывает границу backend: после root exit
в Job ещё может оставаться процесс завершения console host ОС. Отдельные тесты потомка
проверяют принятую отмену при живой группе и отклонённый запрос после подтверждённого успеха.

```powershell
dotnet build ResearchLab.slnx --no-restore
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.RunExecutionTests --filter-class ResearchLab.IntegrationTests.WindowsNativeLauncherTests
dotnet test --solution ResearchLab.slnx --no-build
dotnet format ResearchLab.slnx --no-restore --verify-no-changes
git diff --check
```

Итоговая проверка 2026-10-25, Windows x64 / .NET 10: сборка без предупреждений/ошибок;
полный solution — **281/281 тестов, без пропусков**; format verification и diff whitespace
checks прошли. До реализации отдельно перепроверено предусловие M3.7b: 8/8 тестов прошли.
Промежуточные выборочные прогоны выявили ошибки настройки часов/runtime-файлов fixture и
устаревшее root-only ожидание поздней отмены; всё исправлено до итогового полного прогона.
Другие архитектуры/версии Windows и Unix здесь не запускались.

M3.7d теперь проверяет [реальную CLI crash/persistence matrix](windows-crash-safety.ru.md).
Далее описана граница проверок на момент контрольной точки M3.7c:
рабочий путь был готов к crash tests M3.7d. Отдельный owner-death gate M3.7b сохранён, но этот
этап не заявляет полную матрицу crash/recovery CLI. M3.7d должен завершать настоящий owner
около native creation/сохранения Running/root exit/терминального сохранения, проверять
остановку потомков и явный Interrupted recovery без kill по сохранённым PID, а также потерю
owner во время retained cleanup. Recovery по-прежнему фиксирует потерю владельца, а не
восстановленный результат исполнения или сохранённое доказательство завершения группы.

Официальные ссылки Microsoft Learn: [accounting и lifetime ссылок](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information),
[QueryInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject),
[TerminateJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-terminatejobobject),
[асинхронная остановка и lifetime объекта](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess),
[наследование, signaling и kill-on-close](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).


M3.7e сводит итоговые проверки, область ОС, crash-гарантии и оставшиеся ограничения:
[окончательная проверка (2026-10-25)](m3-final-verification.ru.md). Прежние числа на этой
странице относятся к соответствующему этапу, а не к последнему полному набору тестов.
