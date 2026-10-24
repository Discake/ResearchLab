param(
    [string]$Directory = (Join-Path ([IO.Path]::GetTempPath()) ("ResearchLab-Demo-" + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cli = Join-Path $repository 'src/ResearchLab.Cli/bin/Debug/net10.0/researchlab.dll'
$payload = Join-Path $PSScriptRoot 'bin/Debug/net10.0'
if (!(Test-Path -LiteralPath $cli) -or !(Test-Path -LiteralPath (Join-Path $payload 'researchlab-demo.dll'))) {
    throw 'Build ResearchLab.slnx in Debug first.'
}
if (Test-Path -LiteralPath $Directory) {
    throw 'Choose a new demo directory; existing data is never overwritten.'
}
$root = [IO.Path]::GetFullPath($Directory)
$workspace = Join-Path $root 'workspace'
$database = Join-Path $root 'research.db'
$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Path $workspace | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $payload -File) {
    Copy-Item -LiteralPath $file.FullName -Destination $workspace
}
$executable = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'researchlab-demo.exe' } else { 'researchlab-demo' }
@{
    schemaVersion = 1
    tasks = @{ benchmark = @{ executable = "./$executable"; args = @(); timeoutSeconds = 60 } }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workspace 'researchlab.json') -Encoding utf8

function Invoke-Lab([string[]]$Words) {
    $response = & dotnet $cli --database $database --data-dir $logs @Words
    if ($LASTEXITCODE -ne 0) { throw "CLI preparation failed with exit code $LASTEXITCODE." }
    $response | ConvertFrom-Json
}

Invoke-Lab -Words @('database', 'init') | Out-Null
$project = Invoke-Lab -Words @('project', 'import', $workspace, '--name', 'Foreground demo')
$experiment = Invoke-Lab -Words @('experiment', 'create', '--project', $project.id, '--definition', (Join-Path $PSScriptRoot 'experiment.json'))
@{ cli = $cli; database = $database; logs = $logs; workspace = $workspace; experimentId = $experiment.id } | ConvertTo-Json
