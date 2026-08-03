Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$DisplayName = 'Thai ID Card Local Agent',
    [string]$ProgramPath = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [string]$PublishPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\publish\win-x64')
)

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are required.' }

$configPath = Join-Path $ProgramDataPath 'Config'
$logPath = Join-Path $ProgramDataPath 'Logs'
$exePath = Join-Path $ProgramPath 'ThaiIdCardAgent.Service.exe'

if ($PSCmdlet.ShouldProcess($ProgramPath, 'Install ThaiIdCardAgent Windows Service')) {
    New-Item -ItemType Directory -Force -Path $ProgramPath, $configPath, $logPath | Out-Null
    Copy-Item -Path (Join-Path $PublishPath '*') -Destination $ProgramPath -Recurse -Force
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "Service already exists: $ServiceName" }
    sc.exe create $ServiceName binPath= "`"$exePath`"" DisplayName= $DisplayName start= delayed-auto obj= "NT AUTHORITY\LocalService" | Out-Null
    sc.exe description $ServiceName 'Local loopback API for Thai ID smart card readers.' | Out-Null
    sc.exe config $ServiceName start= delayed-auto | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/none/0 | Out-Null
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
    Invoke-RestMethod -Uri 'https://127.0.0.1:18443/api/v1/health' -Method Get | Out-Null
}
