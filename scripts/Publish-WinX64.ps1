#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$OutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $root 'artifacts\publish\win-x64' } else { $OutputPath }
$project = Join-Path $root 'src\ThaiIdCardAgent.Service\ThaiIdCardAgent.Service.csproj'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

if (-not (Test-Path -LiteralPath $project)) {
    throw "Service project was not found: $project"
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

$args = @(
    'publish', $project,
    '-c', $Configuration,
    '-m:1', '/nr:false',
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-o', $output
)
& dotnet @args
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Join-Path $output 'ThaiIdCardAgent.Service.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish completed but executable was not found: $exe"
}

Write-Host "Published to $output"
Write-Host "Executable: $exe"
