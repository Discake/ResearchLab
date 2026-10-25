# ResearchLab — CLI quickstart and recovery check (Windows)

A practical M3 smoke-test guide. For detailed contracts see [foreground execution](foreground-run.md), [recovery](run-recovery.md), [M3.6](local-run-completion.md), and [M3 verification](m3-final-verification.md).

**Verified target:** Windows 11 x64, .NET 10, PowerShell 7.3+ (recommended). Run from the repository root on `chore/bootstrap`. Each `run start` shows an executable/trust review: inspect it and enter **`yes`** to approve that invocation only.

## 1. Build and prepare a fresh demo

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

The script creates a **new temporary** workspace and SQLite database, imports a Project and creates an Experiment. It does **not** run the sample or approve trust. Keep `$demo` and `$common` in the current PowerShell session. PowerShell 5.1 handles native JSON quoting differently; use PowerShell 7.3+ for the commands below, especially values containing spaces.

## 2. Successful run

```powershell
$run = & dotnet $demo.cli @common run start $demo.experimentId --parameters '{"iterations":4,"scale":2,"label":"first","verbose":true}' | ConvertFrom-Json
$run | ConvertTo-Json -Depth 8
```

Review the executable and fingerprint; enter `yes`. Expect `Succeeded`, Run `exitCode: 0` and `Sum: 20` in `logOutput.stdout`. This is a demonstration, not a benchmark measurement.

## 3. Persisted history and logs

```powershell
& dotnet $demo.cli @common run show $run.id --include-logs
& dotnet $demo.cli @common run list --experiment $demo.experimentId
```

These reopen the stored Run. Each Run has `stdout.log` and `stderr.log` beneath `<data-dir>/<RunId without hyphens>/`, plus a dedicated `output` directory. The simple sum demo does not register artifacts.

## 4. Intentional failure

```powershell
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"fail":true}'
$LASTEXITCODE
```

Approve with `yes`. Expect Run `Failed` and **Run exit code 7** in the output JSON; the **CLI process exit code is 6**. Both stdout and stderr remain available.

## 5. Ctrl+C cancellation

```powershell
& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"delayMilliseconds":30000}'
```

Approve, then press **Ctrl+C once** during execution. The CLI should request cancellation, await confirmed cleanup, persist `Canceled`, return CLI code 130 and keep the Run exit code null. Do not substitute `Stop-Process`, which tests a different failure mode.

## 6. Quick timeout (optional)

The sample task normally has a 60-second timeout. For a fast check change **only the temporary workspace** to one second:

```powershell
$configPath = Join-Path $demo.workspace 'researchlab.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.tasks.benchmark.timeoutSeconds = 1
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

& dotnet $demo.cli @common run start $demo.experimentId --parameters '{"delayMilliseconds":30000}'
$LASTEXITCODE
```

Inspect the **changed fingerprint**, approve with `yes`, and expect Run `TimedOut`, CLI exit code 124, Run exit code null. Rerun the demo preparation script for a fresh workspace with the original timeout.

## 7. Explicit recovery after owner loss

```powershell
$recovery = & dotnet $demo.cli @common run recover | ConvertFrom-Json
$recovery | ConvertTo-Json -Depth 6
```

Recovery is **not automatic**. The result exposes:

| Field | Meaning |
| --- | --- |
| `recovered` | Run IDs safely marked `Interrupted` after verified owner loss |
| `conflicts` | Runs with concurrent version conflicts needing later inspection |
| `unverifiedSessions` | Active or unverifiable sessions deliberately left unchanged |

**After ordinary terminal runs all three arrays are normally empty.** That is correct. CLI exit code 0 indicates that the recovery command completed, not that these lists are empty; inspect them.

Repeat safely:

```powershell
& dotnet $demo.cli @common run recover
```

Recovered Runs can be viewed with `run show <run-id>`. `Interrupted` records verified ownership loss, not a recovered execution result. Recovery neither restarts work nor kills arbitrary persisted PIDs.

### Test actual crash/recovery safely

Ctrl+C produces `Canceled`, not `Interrupted`. Instead of killing an arbitrary `dotnet.exe` process, use the repository's isolated Windows crash tests: these kill **only a test-owned CLI**, check its contained process group and explicitly run recovery.

```powershell
dotnet test --project tests/ResearchLab.IntegrationTests --no-build -- --filter-class ResearchLab.IntegrationTests.WindowsCrashSafetyTests
```

An empty `run recover` response alone does not test crash recovery.

## 8. M3 scope

The CLI checks ordinary execution, Run statuses, logs, history, cancellation, timeout and idempotent recovery. Separate Windows crash tests cover abrupt owner death. Windows versions beyond the tested configuration and Linux/macOS remain unverified. Job Objects provide process-group ownership, **not a filesystem/network sandbox**.

Next: **M4 structured metrics and registered artifacts**.
