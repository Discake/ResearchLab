#requires -Version 7.3
param([string]$Directory = (Join-Path ([IO.Path]::GetTempPath()) ('ResearchLab-Metrics-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$repository = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $repository 'src/ResearchLab.Cli/bin/Debug/net10.0/researchlab.dll'
$payload = Join-Path $repository 'tests/ResearchLab.ProcessFixture/bin/Debug/net10.0'
if (!(Test-Path $cli) -or !(Test-Path "$payload/ResearchLab.ProcessFixture.dll")) { throw 'Build ResearchLab.slnx in Debug first.' }
if (Test-Path -LiteralPath $Directory) { throw 'Choose a new directory.' }
$root = [IO.Path]::GetFullPath($Directory)
$workspace = Join-Path $root 'workspace'
New-Item -ItemType Directory $workspace | Out-Null
Get-ChildItem -LiteralPath $payload -File | Copy-Item -Destination $workspace
$exe = if ($IsWindows) { './ResearchLab.ProcessFixture.exe' } else { './ResearchLab.ProcessFixture' }
@{ schemaVersion = 1; tasks = @{ demo = @{ executable = $exe; args = @('metric-demo'); timeoutSeconds = 30 } } } |
    ConvertTo-Json -Depth 6 | Set-Content "$workspace/researchlab.json" -Encoding utf8NoBOM
@{ name = 'Metric calculation demo'; description = 'Not a benchmark'; taskKey = 'demo'; parameters = @(
    @{ name = 'n'; type = 'int'; required = $true; minimum = 1; maximum = 10 }
) } | ConvertTo-Json -Depth 6 | Set-Content "$root/experiment.json" -Encoding utf8NoBOM
$common = @('--database', "$root/research.db", '--data-dir', "$root/logs")
function Invoke-Lab([string[]]$Words) {
    $result = & dotnet $cli @common @Words
    if ($LASTEXITCODE -ne 0) { throw "CLI exit code: $LASTEXITCODE" }
    $result | ConvertFrom-Json
}
Invoke-Lab @('database', 'init') | Out-Null
$project = Invoke-Lab @('project', 'import', $workspace, '--name', 'Metric demo')
$experiment = Invoke-Lab @('experiment', 'create', '--project', $project.id, '--definition', "$root/experiment.json")
# CLI itself presents the trust review and reads the user's explicit yes from stdin.
$run = Invoke-Lab @('run', 'start', $experiment.id, '--parameters', '{"n":2}')
Write-Host "Saved Run ID: $($run.id)"
$page = Invoke-Lab @('run', 'metrics', $run.id)
$page | ConvertTo-Json -Depth 8
if ($page.runId -ne $run.id -or $page.hasMore -or $page.points.Count -ne 4 -or
    ($page.points.sequence -join ',') -ne '1,2,3,4' -or ($page.points.value -join ',') -ne '4,4,16,2' -or
    ($page.points.name -join ',') -ne 'demo_square,demo_square,demo_square,demo_input' -or
    ($page.points[0..2].step -join ',') -ne '1,1,2') { throw 'Unexpected saved metric evidence.' }
$filtered = Invoke-Lab @('run', 'metrics', $run.id, '--name', 'demo_square', '--offset', '2', '--page-size', '1')
if ($filtered.points.Count -ne 1 -or $filtered.points[0].sequence -ne 3 -or $filtered.points[0].value -ne 16 -or $filtered.hasMore) { throw 'Pagination failed.' }
Write-Host "Metric smoke passed. Private files retained at $root"
