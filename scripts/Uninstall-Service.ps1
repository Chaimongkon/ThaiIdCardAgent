#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$ProgramPath = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [switch]$RemoveData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $WhatIfPreference) {
    throw 'Administrator rights are required.'
}

$resolvedProgramPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramPath)
$resolvedProgramDataPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramDataPath)
if ((Split-Path -Leaf $resolvedProgramPath) -ne 'ThaiIdCardAgent') {
    throw "Refusing to remove unexpected program path: $resolvedProgramPath"
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Uninstall ThaiIdCardAgent Windows Service')) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        }

        sc.exe delete $ServiceName | Out-Null
    }

    if (Test-Path -LiteralPath $resolvedProgramPath) {
        Remove-Item -LiteralPath $resolvedProgramPath -Recurse -Force
    }

    if ($RemoveData -and (Test-Path -LiteralPath $resolvedProgramDataPath)) {
        Remove-Item -LiteralPath $resolvedProgramDataPath -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $resolvedProgramDataPath) {
        Write-Host "Keeping config and logs: $resolvedProgramDataPath"
    }

    Write-Host "Uninstalled service: $ServiceName"
}
