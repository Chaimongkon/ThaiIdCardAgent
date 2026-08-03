[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$DisplayName = 'Thai ID Card Local Agent',
    [string]$Description = 'Local service for connecting authorized web applications to a PC/SC smart card reader.',
    [string]$ProgramPath = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [string]$PublishPath = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\publish\win-x64'),
    [string]$HealthUri = 'https://127.0.0.1:18443/api/v1/health'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
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

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service already exists: $ServiceName"
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Install ThaiIdCardAgent Windows Service')) {
    New-Item -ItemType Directory -Force -Path $resolvedProgramPath, $configPath, $logPath | Out-Null

    if (Test-Path -LiteralPath $configPath) {
        $backupPath = Join-Path $resolvedProgramDataPath ("Config.backup.{0:yyyyMMddHHmmss}" -f (Get-Date))
        Copy-Item -LiteralPath $configPath -Destination $backupPath -Recurse -Force
        Write-Host "Config backup: $backupPath"
    }

    Copy-Item -LiteralPath (Join-Path $resolvedPublishPath '*') -Destination $resolvedProgramPath -Recurse -Force

    icacls $resolvedProgramDataPath /grant 'NT AUTHORITY\LOCAL SERVICE:(OI)(CI)(M)' /T | Out-Null

    sc.exe create $ServiceName binPath= "`"$exePath`"" DisplayName= "$DisplayName" start= delayed-auto obj= 'NT AUTHORITY\LocalService' | Out-Null
    sc.exe description $ServiceName "$Description" | Out-Null
    sc.exe config $ServiceName start= delayed-auto | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/none/0 | Out-Null

    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3

    try {
        Invoke-RestMethod -Uri $HealthUri -Method Get -TimeoutSec 10 | Out-Null
    }
    catch {
        Write-Error "Service installed but health check failed: $($_.Exception.Message)"
        exit 1
    }

    Write-Host "Installed service: $ServiceName"
}
