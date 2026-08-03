Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ((Split-Path $root -Leaf) -ne 'ThaiIdCardAgent') { throw "Repository root must be named ThaiIdCardAgent: $root" }
if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) { throw "Missing .git in $root" }
foreach ($path in @('src','tests','scripts','examples','docs','artifacts')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $root $path) | Out-Null
}
Write-Host "Initialized repository root: $root"
