# Read-only console host

ResearchLab.Cli is the composition root for the existing ProjectCatalog, RunHistory and
RunLogs services. It contains argument parsing, response formatting and exit-code handling.
It adds no domain rules, migration runner, owner session, recovery or execution command.

The application executable is named `researchlab`. From a built checkout, invoke it with
`dotnet run --no-build --project src/ResearchLab.Cli -- <arguments>` or
`dotnet src/ResearchLab.Cli/bin/Debug/net10.0/researchlab.dll <arguments>`.
This is a local executable, not an installed global tool.

Every read command requires `--database <sqlite-file>` and `--data-dir <log-root>`.
Options may precede or follow the command. Quote paths containing spaces. Relative paths
resolve against the calling process's current directory, not the database directory.
There are no implicit database locations or environment-variable defaults.
The data directory is exactly the private root previously passed to RunLogFiles, not a
workspace, a generic M4 artifact store or an automatically appended `logs` subdirectory.
A missing log root is allowed and is not created; `run show` then reports `logs: null`.
An existing file used as the data directory is rejected.

Supported command arguments:

```text
project list
experiment list --project <project-id>
run list --experiment <experiment-id> [--offset <n>] [--page-size <n>]
run show <run-id>
--help
```

The filters are required; IDs must be nonempty GUIDs. Run pagination uses RunPageRequest
and RunHistory: defaults 0/50, size 1–100, CreatedAtUtc descending then ID ascending.
The CLI does not reimplement sorting or pagination, or silently read every history page.
Offset pagination has the existing concurrency limitations described in [Run history](run-history.md).

Success writes one indented JSON value to stdout: arrays for lists (including `[]`), an
object for show. Project output omits workspace paths. Run output uses Application's safe
diagnostics, recorded parameters and dates/exit code; show also includes known relative
stdout/stderr paths through RunLogs, without reading log contents. JSON escaping prevents
stored control characters from becoming terminal control sequences. Recorded parameters
may themselves contain private user input; this is a local inspection tool, not a public export.

Reads open SQLite with Mode=ReadOnly. A missing, empty, corrupt or incompatible database
is an error; the CLI never creates a database or runs migrations. No file deletion or task
execution is performed. Log lookup retains the existing containment/link checks and trusted
private-root TOCTOU limitations. Explicitly choose the intended existing database and log root.

Errors go to stderr, with no exception dumps, raw database diagnostics or private host paths:

| Exit code | Meaning |
| --- | --- |
| 0 | Success, including an empty list or standalone --help/-h |
| 1 | Unexpected read failure |
| 2 | Invalid/incomplete command, options, GUID, pagination or missing configuration |
| 3 | Project, Experiment or Run was not found |
| 4 | Filesystem/access, SQLite, stored-data or unsafe log-path error |
| 130 | Read canceled (including Ctrl+C) |

Tests use synthetic temporary databases, exercise parsing/error cases and Application reads,
verify database bytes and session state are unchanged, reject writes through the read-only
store, and execute all four commands through the actual console entry point in child processes.
No benchmark results or product research evidence are created by these tests.
