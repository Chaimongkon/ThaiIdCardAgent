[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$DisplayName = 'Thai ID Card Local Agent',
    [string]$Description = 'Local service for connecting authorized web applications to a PC/SC smart card reader.',
    [string]$ProgramPath = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [string]$PublishPath = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\publish\win-x64'),
    [string]$HealthUri = 'https://127.0.0.1:18443/api/v1/health',
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService',
    [switch]$SkipStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $WhatIfPreference) {
    throw 'Administrator rights are required.'
}

$resolvedPublishPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishPath)
$resolvedProgramPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramPath)
$resolvedProgramDataPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramDataPath)
$configPath = Join-Path $resolvedProgramDataPath 'Config'
$logPath = Join-Path $resolvedProgramDataPath 'Logs'
$exePath = Join-Path $resolvedProgramPath 'ThaiIdCardAgent.Service.exe'
$sourceExe = Join-Path $resolvedPublishPath 'ThaiIdCardAgent.Service.exe'

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Publish output was not found. Run scripts\Publish-WinX64.ps1 first: $sourceExe"
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$action = if ($service) { 'Upgrade ThaiIdCardAgent Windows Service' } else { 'Install ThaiIdCardAgent Windows Service' }

if ($PSCmdlet.ShouldProcess($ServiceName, $action)) {
    New-Item -ItemType Directory -Force -Path $resolvedProgramPath, $configPath, $logPath | Out-Null

    if (Test-Path -LiteralPath $configPath) {
        $configItems = Get-ChildItem -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
        if ($configItems) {
            $backupPath = Join-Path $resolvedProgramDataPath ("Config.backup.{0:yyyyMMddHHmmss}" -f (Get-Date))
            Copy-Item -LiteralPath $configPath -Destination $backupPath -Recurse -Force
            Write-Host "Config backup: $backupPath"
        }
    }

    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping service before copy: $ServiceName"
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    try {
        Get-ChildItem -LiteralPath $resolvedPublishPath -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $resolvedProgramPath -Recurse -Force -ErrorAction Stop
        }
    }
    catch {
        throw "Failed to copy publish output to $resolvedProgramPath. Service was not reconfigured. Error: $($_.Exception.Message)"
    }

    $aclAccount = if ($ServiceAccount -eq 'NT AUTHORITY\LocalService') { 'NT AUTHORITY\LOCAL SERVICE' } else { $ServiceAccount }
    icacls $resolvedProgramDataPath /grant "${aclAccount}:(OI)(CI)(M)" /T | Out-Null

    if (-not $service) {
        sc.exe create $ServiceName binPath= "`"$exePath`"" DisplayName= "$DisplayName" start= delayed-auto obj= $ServiceAccount | Out-Null
    }
    else {
        sc.exe config $ServiceName binPath= "`"$exePath`"" DisplayName= "$DisplayName" start= delayed-auto obj= $ServiceAccount | Out-Null
    }

    sc.exe description $ServiceName "$Description" | Out-Null
    sc.exe config $ServiceName start= delayed-auto | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/none/0 | Out-Null

    if (-not $SkipStart) {
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 3

        try {
            Invoke-RestMethod -Uri $HealthUri -Method Get -TimeoutSec 10 | Out-Null
        }
        catch {
            Write-Error "Service installed or upgraded but health check failed: $($_.Exception.Message)"
            exit 1
        }
    }

    Write-Host "Installed or upgraded service: $ServiceName"
}
