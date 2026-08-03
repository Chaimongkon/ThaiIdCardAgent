Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$ProgramPath = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [switch]$RemoveData
)

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are required.' }

if ($PSCmdlet.ShouldProcess($ServiceName, 'Uninstall ThaiIdCardAgent Windows Service')) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
        sc.exe delete $ServiceName | Out-Null
    }
    if (Test-Path -LiteralPath $ProgramPath) { Remove-Item -LiteralPath $ProgramPath -Recurse -Force }
    if ($RemoveData -and (Test-Path -LiteralPath $ProgramDataPath)) { Remove-Item -LiteralPath $ProgramDataPath -Recurse -Force }
}
