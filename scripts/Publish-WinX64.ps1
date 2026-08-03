Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\ThaiIdCardAgent.Service\ThaiIdCardAgent.Service.csproj'
$output = Join-Path $root 'artifacts\publish\win-x64'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $output
Write-Host "Published to $output"
