# Foreground experiment execution through the CLI

The existing `researchlab` executable now composes ProjectCatalog, RunExecution/ProcessRunner,
RunHistory and RunLogs. There is no additional host, daemon, queue or detached driver. This
guide is reproducible from a fresh checkout using the supplied real console example; no
C# host code or hand-edited SQLite records are needed. Windows/PowerShell 7 is verified.

## From an empty directory to a saved Run

From the repository root:

```powershell
dotnet restore ResearchLab.slnx
dotnet build ResearchLab.slnx --no-restore
$demo = & ./samples/foreground-demo/prepare-demo.ps1 | ConvertFrom-Json
$common = @('--database', $demo.database, '--data-dir', $demo.logs)
$demo | Format-List
$run = & dotnet $demo.cli @common run start $demo.experimentId --parameters '{"iterations":4,"scale":2,"label":"first","verbose":true}' | ConvertFrom-Json
```

Read the review on stderr: the absolute executable path, literal arguments, timeout,
fingerprint and validated parameters (including defaults). Type **yes** and Enter to
approve this invocation. Any other response or EOF declines without creating a Run.
There is no `--yes` switch or persistent trust setting. A changed executable/configuration
between review, confirmation and launch is rejected through the existing Application checks.
Trust covers the declared entry point/configuration, not every dependency or an OS sandbox.

The command waits for execution and persistence, then returns a JSON object. The example
actually computes the scaled sum 1+2+3+4; with scale 2 its stdout contains `Sum: 20`.
This is a local execution demonstration, not a benchmark or recorded research metrics.

```powershell
$run | ConvertTo-Json -Depth 8
# A different CLI invocation reopens the same saved Run and its captured logs:
& dotnet $demo.cli @common run show $run.id --include-logs
& dotnet $demo.cli @common run list --experiment $demo.experimentId
```

Output includes Run ID, status, recorded parameters/timestamps, exit code, Application's
safe diagnostic, relative log paths and `logOutput.stdout`/`stderr`. Log text is UTF-8 with
replacement for invalid bytes and JSON-escaped, including control/ANSI characters. Capture
keeps the existing 1 MiB combined byte cap; CLI display also caps each stream to 1 MiB of
characters and reports `logOutput.truncated` if private files were enlarged after capture.
`run show` without `--include-logs` keeps the lightweight metadata/path response.
Log contents/parameters may contain private local data; this is not a public export.
Logs are shown after completion; this increment does not add live tailing.

The preparation script creates a new temporary directory, copies the built example,
writes `researchlab.json`, then uses only these CLI operations:

```text
database init
project import <workspace> --name <name>
experiment create --project <project-id> --definition <experiment-json-file>
```

It returns the actual paths and generated IDs as JSON and does not execute the task or
approve trust. `-Directory <new-directory>` chooses another location; existing directories
are rejected rather than overwritten. Keep the private database/log root outside the
imported workspace. The example's declaration has a 60-second timeout.

For a different program, declare a native executable, literal args and timeout in
`researchlab.json`, import its workspace, and supply an Experiment definition as below.
On Windows the existing runner accepts `.exe` entry points, not `.cmd`/`.bat` shell fallback.
On Unix use the executable name without `.exe` and appropriate execute permissions; Unix
execution has not been verified in this environment. The preparation script selects the
platform's built example entry point automatically.

## Parameters and metadata preparation

`--parameters` is one JSON object, quoted as a single shell argument. Values are converted
using the persisted Experiment schema: int32, finite double, JSON bool or string. Numeric
JSON values for declared doubles are converted to double even when written as `2`.
Strings are not coerced to numbers/bools. Null, arrays/objects as values, duplicate/unknown
names, missing required values, wrong types, disallowed values and out-of-range inputs
fail before trust confirmation or Run insertion. Defaults and all constraints belong to
Domain. Parameters are passed to the child through `RESEARCHLAB_PARAMETERS_JSON`, never
interpolated into task arguments or a shell command.

`experiment create --definition` reads a JSON file. Its shape maps directly to the existing
Experiment model; see [the full example](../samples/foreground-demo/experiment.json):

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

`parameters` may be omitted for a parameterless experiment. Each entry supports `name`,
`type` (int/double/bool/string), `required`, `default`, `minimum`, `maximum`, `allowedValues`.
Unknown definition fields are rejected. `project import` and `experiment create` delegate
to ProjectCatalog; they do not run declared tasks or create trust. Only explicit
`database init` creates/migrates SQLite. Every other command requires an existing compatible
database; none silently initializes/migrates or runs recovery. Existing read commands still
use SQLite Mode=ReadOnly. Write commands open the existing database Mode=ReadWrite.
All commands keep explicit `--database` and `--data-dir`, before or after the command.

## Ctrl+C, failures and ownership

```powershell
# After reviewing and typing yes, press Ctrl+C while the example is waiting:
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"delayMilliseconds":30000}'
# A real nonzero child exit, with retained stdout/stderr and exit code 7:
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"fail":true}'
```

Each start asks for trust again, including these examples. After Created is saved the
CLI retains the RunOperation and driver task. Program's Ctrl+C handler suppresses default
termination and turns its request token into `RequestCancellation()` on that operation.
The CLI waits without canceling execution/status persistence and emits the recorded result.
Repeated Ctrl+C requests do not cause an immediate unsafe exit. A late Ctrl+C cannot
rewrite an already selected natural exit or timeout. Canceled/TimedOut keep a null domain
exit code. Before ownership, Ctrl+C cancels preparation/interactive confirmation instead.

Each Execution cleanup wait keeps its existing bound. If termination/collection is
unconfirmed, the CLI stays in the foreground with the same owner and retries **cleanup**
after one second, never starting another process. It reports that no terminal outcome is
claimed; a permanent OS denial can keep the command waiting until the process ends or
cleanup succeeds. No background retry worker exists. Terminal database-write failure is
reported as a storage error with the already displayed Run ID, not a false saved result.
Killing the host, closing the console forcibly or terminating its entire process group
can bypass Ctrl+C handling; [M3.3 recovery](run-recovery.md) remains explicit and records
verified ownership loss, not guaranteed cleanup of orphan descendants.

| Exit | Meaning |
| --- | --- |
| 0 | Successful command / Succeeded Run |
| 1 | Unexpected operation failure |
| 2 | Usage or input/domain validation error |
| 3 | Requested Project/Experiment/Run missing |
| 4 | Storage, access, configuration or persistence error |
| 5 | Trust declined, absent or changed |
| 6 | Saved Failed Run (the child exit code is separately shown in JSON) |
| 124 | Saved TimedOut Run |
| 130 | Saved Canceled Run, or cancellation before ownership |

Integration tests run the example through the real CLI entry point from empty SQLite,
reopen it in another invocation, reject EOF/no consent, recheck changed task/executable,
validate typed inputs, distinguish nonzero exit/timeout/cancellation, and verify owner
retention and terminal persistence. Windows console-control-event coverage exercises the
actual Program/CancelKeyPress path in an isolated hidden **test** console.

For explicit `run recover` and the program CWD/RESEARCHLAB_OUTPUT_DIR contract, see [M3.6](local-run-completion.md).
