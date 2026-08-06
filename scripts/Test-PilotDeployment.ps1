#requires -Version 5.1
<#
.SYNOPSIS
    Clean-machine pilot deployment acceptance for ThaiIdCardAgent, driven entirely from a
    release ZIP (no source tree required on the target machine).

.DESCRIPTION
    Extracts a release ZIP to a temporary directory, verifies integrity (manifest, SHA-256
    checksums, secret exclusion, signing status), then optionally installs and exercises the
    service and hardware. Results are reported as Passed / Failed / Not Tested. Hardware steps
    are interactive and skippable; a skipped hardware step is reported Not Tested, never Passed.
    A failure is never reported as Passed.

    Modes:
      Full        Verify + install + service + hardware + restart + upgrade + uninstall/reinstall.
      VerifyOnly  Verify integrity only (no install; safe without Administrator / hardware).
      Tamper      Verify that a modified package copy is rejected before install; original ZIP
                  and any existing install/config are left untouched.
      Rollback    Simulate upgrade failures (copy failure, invalid manifest, checksum mismatch)
                  against a temporary install directory and confirm the previous files return.

.NOTES
    Windows PowerShell 5.1 compatible. Supports -WhatIf. Never writes JWTs, private keys, PFX
    passwords, Authorization headers, or PII to output. The source ZIP is never modified.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseZipPath,
    [ValidateSet('Full', 'VerifyOnly', 'Tamper', 'Rollback', 'PostReboot')][string]$Mode = 'Full',
    [string]$InstallDirectory = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$CertificateThumbprint,
    [string]$CertificateHostName = 'localhost',
    [string]$BaseUrl = 'https://localhost:18443',
    [string]$AllowedOrigin = 'http://localhost:3000',
    [string]$JwtPublicKeyPath,
    [string]$JwtPrivateKeyPath,
    # Path to a pre-published ThaiIdCardAgent.TestJwt.exe so a clean machine needs no .NET SDK or
    # source tree. When omitted, falls back to scripts\New-TestJwt.ps1 (repo/dev machines only).
    [string]$JwtToolPath,
    # A DIFFERENT release ZIP (e.g. 0.1.1-pilot) used for genuine version-upgrade acceptance.
    [string]$UpgradeZipPath,
    [switch]$RequireSigned,
    [switch]$SkipHardware,
    [switch]$SkipUpgrade,
    [switch]$SkipUninstallReinstall,
    [switch]$KeepExtractedPackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

$script:results = New-Object System.Collections.Generic.List[object]
$script:failed = $false
$script:activeConfigMarker = $null
$script:activeLogMarker = $null

$ReleaseZipPath = if ([string]::IsNullOrWhiteSpace($ReleaseZipPath)) { $ReleaseZipPath } else { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseZipPath) }
$UpgradeZipPath = if ([string]::IsNullOrWhiteSpace($UpgradeZipPath)) { $UpgradeZipPath } else { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($UpgradeZipPath) }
$JwtPublicKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPublicKeyPath)) { $JwtPublicKeyPath } else { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPublicKeyPath) }
$JwtPrivateKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPrivateKeyPath)) { $JwtPrivateKeyPath } else { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPrivateKeyPath) }

if ([string]::IsNullOrWhiteSpace($JwtToolPath)) {
    $localTool = Join-Path $PSScriptRoot 'ThaiIdCardAgent.TestJwt.exe'
    if (Test-Path -LiteralPath $localTool -PathType Leaf) {
        $JwtToolPath = (Resolve-Path -LiteralPath $localTool).Path
    }
}
else {
    $JwtToolPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtToolPath)
}

$script:JwtPublicKeyPath = $JwtPublicKeyPath
$script:JwtPrivateKeyPath = $JwtPrivateKeyPath
$script:JwtToolPath = $JwtToolPath

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Message = '')
    if ($Status -eq 'Failed') { $script:failed = $true }
    $script:results.Add([pscustomobject]@{ Step = $Name; Status = $Status; Message = $Message })
    Write-Host "[$Status] $Name $Message"
}

function Complete-Acceptance {
    Write-Host ''
    Write-Host "Pilot deployment acceptance summary (Mode=$Mode)"
    $script:results | Format-Table -AutoSize | Out-Host
    $passed = @($script:results | Where-Object { $_.Status -eq 'Passed' }).Count
    $failedCount = @($script:results | Where-Object { $_.Status -eq 'Failed' }).Count
    $notTested = @($script:results | Where-Object { $_.Status -eq 'Not Tested' }).Count
    Write-Host "Passed=$passed  Failed=$failedCount  NotTested=$notTested"
    if ($script:failed) { exit 1 } else { exit 0 }
}

function Test-IsAdministrator {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-FileSha256 {
    # Computed with .NET directly: Get-FileHash returns nothing under -WhatIf (its provider
    # path resolution is WhatIf-suppressed), which would break the preflight ZIP hash.
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Get-InstalledManifestVersion {
    param([string]$ManifestPath)
    if (-not (Test-Path -LiteralPath $ManifestPath)) { return '<none>' }
    try { return [string]((Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json).version) }
    catch { return '<unreadable>' }
}

function Test-JwtKeysUsable {
    if ([string]::IsNullOrWhiteSpace($script:JwtPublicKeyPath) -or [string]::IsNullOrWhiteSpace($script:JwtPrivateKeyPath)) { return $false }
    if ($script:JwtPublicKeyPath -match '<[^>]+>' -or $script:JwtPrivateKeyPath -match '<[^>]+>') { return $false }
    return (Test-Path -LiteralPath $script:JwtPublicKeyPath -PathType Leaf) -and (Test-Path -LiteralPath $script:JwtPrivateKeyPath -PathType Leaf)
}

function New-TestToken {
    # Creates a short-lived JWT. The token is never printed.
    #   -JwtToolPath  -> run a pre-published ThaiIdCardAgent.TestJwt.exe (no SDK/source needed).
    #   otherwise     -> fall back to scripts\New-TestJwt.ps1 (dotnet run; repo/dev only).
    $tokenPath = Join-Path $env:TEMP ("tia-pilot-token-" + [guid]::NewGuid().ToString('N') + ".jwt")
    try {
        if (-not [string]::IsNullOrWhiteSpace($script:JwtToolPath)) {
            $toolArgs = @(
                '--private-key', $script:JwtPrivateKeyPath, '--public-key', $script:JwtPublicKeyPath,
                '--token-output', $tokenPath, '--issuer', 'thai-id-card-agent-client',
                '--audience', 'thai-id-card-agent', '--subject', 'pilot-acceptance',
                '--workstation-id', $env:COMPUTERNAME, '--lifetime-seconds', '60', '--force'
            )
            & $script:JwtToolPath @toolArgs | Out-Null
        }
        else {
            $jwtArgs = @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'New-TestJwt.ps1'),
                '-PrivateKeyPath', $script:JwtPrivateKeyPath, '-PublicKeyPath', $script:JwtPublicKeyPath,
                '-TokenOutputPath', $tokenPath, '-LifetimeSeconds', '60', '-Force'
            )
            & powershell.exe @jwtArgs | Out-Null
        }
        if ($LASTEXITCODE -ne 0) { throw "JWT tool failed (exit $LASTEXITCODE)." }
        $token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($token)) { throw 'JWT token was empty.' }
        return $token
    }
    finally {
        if (Test-Path -LiteralPath $tokenPath) { Remove-Item -LiteralPath $tokenPath -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-PostRebootChecks {
    # Verifies persisted service state after a real Windows reboot. Never call this before an
    # actual reboot: run -Mode PostReboot only once the machine has rebooted.
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if (-not $svc) { Add-Result 'Reboot: service present' 'Failed' 'Service not installed.'; return }

    if ($svc.State -eq 'Running') { Add-Result 'Reboot: service running' 'Passed' } else { Add-Result 'Reboot: service running' 'Failed' "State=$($svc.State)" }
    if ($svc.StartMode -eq 'Auto') { Add-Result 'Reboot: start mode Auto' 'Passed' } else { Add-Result 'Reboot: start mode Auto' 'Failed' "StartMode=$($svc.StartMode)" }
    $delayed = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name 'DelayedAutostart' -ErrorAction SilentlyContinue).DelayedAutostart
    if ($delayed) { Add-Result 'Reboot: delayed auto-start' 'Passed' } else { Add-Result 'Reboot: delayed auto-start' 'Failed' 'DelayedAutostart not set.' }
    if ($svc.StartName -eq 'NT AUTHORITY\LocalService') { Add-Result 'Reboot: service account' 'Passed' $svc.StartName } else { Add-Result 'Reboot: service account' 'Failed' "Account=$($svc.StartName)" }

    try {
        Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
        Add-Result 'Reboot: HTTPS health' 'Passed' 'No certificate-validation bypass used.'
    }
    catch { Add-Result 'Reboot: HTTPS health' 'Failed' $_.Exception.Message }

    if (-not (Test-JwtKeysUsable)) {
        Add-Result 'Reboot: Readers API' 'Not Tested' 'JWT public/private key paths were not provided.'
        return
    }
    try {
        Invoke-AgentJson -Method Get -Path '/api/v1/readers' | Out-Null
        Add-Result 'Reboot: Readers API' 'Passed'
    }
    catch { Add-Result 'Reboot: Readers API' 'Failed' $_.Exception.Message }
}

function Invoke-AgentJson {
    param([string]$Method, [string]$Path, [object]$Body = $null)
    $token = New-TestToken
    $headers = @{ Authorization = "Bearer $token"; Origin = $script:AllowedOrigin }
    $uri = "$($script:BaseUrl)$Path"
    if ($null -ne $Body) {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 6) -TimeoutSec 15
    }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 15
}

function Wait-ForCardStatus {
    param([string]$ExpectedStatus, [string]$ResultName, [int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $consecutive = 0
    $latest = '<not observed>'
    do {
        try { $latest = [string](Invoke-AgentJson -Method Get -Path '/api/v1/card/status').data.status }
        catch { $latest = "request failed: $($_.Exception.Message)" }
        if ($latest -eq $ExpectedStatus) { $consecutive++ } else { $consecutive = 0 }
        if ($consecutive -ge 2) { Add-Result $ResultName 'Passed' "Observed $ExpectedStatus."; return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    Add-Result $ResultName 'Failed' "Timed out waiting for $ExpectedStatus. Latest: $latest."
}

function Invoke-FullAcceptance {
    param([string]$PackageRoot)

    if ($script:failed) { Add-Result 'Install service' 'Not Tested' 'Integrity failed; not installing.'; return }

    $thumbprint = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $env:Agent__Https__Certificate__Thumbprint } else { $CertificateThumbprint }
    if ([string]::IsNullOrWhiteSpace($thumbprint)) {
        Add-Result 'Install service' 'Failed' 'CertificateThumbprint is required for Full mode.'
        return
    }

    if ($WhatIfPreference) {
        Add-Result 'Install service' 'Not Tested' "WhatIf: would install from package '$PackageRoot' with thumbprint provided."
        foreach ($s in 'Service account', 'Start mode', 'Delayed auto-start', 'Certificate private-key ACL', 'Start service', 'HTTPS health', 'JWT authentication', 'Readers API', 'Card status API', 'Card ATR API', 'Card polling transitions', 'SSE events', 'Restart health/readers', 'Upgrade', 'Config/log retention', 'Uninstall keep data', 'Reinstall') {
            Add-Result $s 'Not Tested' 'WhatIf mode.'
        }
        return
    }

    # 13. Certificate private-key ACL (delegated).
    $aclAccount = 'NT AUTHORITY\LOCAL SERVICE'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Set-CertificatePrivateKeyAcl.ps1') -Thumbprint $thumbprint -Account $aclAccount | Out-Host
    if ($LASTEXITCODE -eq 0) { Add-Result 'Certificate private-key ACL' 'Passed' } else { Add-Result 'Certificate private-key ACL' 'Failed' 'ACL script failed.'; return }

    # 9. Install from package.
    $installArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Install-Service.ps1'),
        '-ServiceName', $ServiceName, '-PackagePath', $PackageRoot,
        '-CertificateThumbprint', $thumbprint, '-CertificateHostName', $CertificateHostName, '-SkipStart'
    )
    if ($RequireSigned) { $installArgs += '-RequireSigned' }
    & powershell.exe @installArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { Add-Result 'Install service' 'Failed' 'Install-Service.ps1 failed.'; return }
    Add-Result 'Install service' 'Passed' 'Installed from release package.'

    # 10-12. Service identity / start mode / delayed autostart.
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    $svcAccount = if ($svc) { $svc.StartName } else { 'not installed' }
    $svcStartMode = if ($svc) { $svc.StartMode } else { 'n/a' }
    if ($svc -and $svc.StartName -eq 'NT AUTHORITY\LocalService') { Add-Result 'Service account' 'Passed' $svc.StartName }
    else { Add-Result 'Service account' 'Failed' "Account=$svcAccount" }
    if ($svc -and $svc.StartMode -eq 'Auto') { Add-Result 'Start mode' 'Passed' 'Automatic' } else { Add-Result 'Start mode' 'Failed' "StartMode=$svcStartMode" }
    $delayed = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name 'DelayedAutostart' -ErrorAction SilentlyContinue).DelayedAutostart
    if ($delayed) { Add-Result 'Delayed auto-start' 'Passed' } else { Add-Result 'Delayed auto-start' 'Failed' 'DelayedAutostart not set.' }

    # 14-15. Start + health.
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
    Add-Result 'Start service' 'Passed'
    Add-Result 'HTTPS health' 'Passed' 'No certificate-validation bypass used.'

    # 16-19. JWT + APIs.
    if (-not (Test-JwtKeysUsable)) {
        foreach ($s in 'JWT authentication', 'Readers API', 'Card status API', 'Card ATR API', 'Card polling transitions', 'SSE events', 'Restart health/readers', 'Upgrade', 'Config/log retention', 'Uninstall keep data', 'Reinstall') {
            Add-Result $s 'Not Tested' 'JWT public/private key paths were not provided.'
        }
        return
    }

    $unauthorized = $false
    try { Invoke-RestMethod -Uri "$BaseUrl/api/v1/readers" -Method Get -TimeoutSec 10 | Out-Null }
    catch { $unauthorized = $true }
    if ($unauthorized) { Add-Result 'JWT authentication' 'Passed' 'Unauthenticated request rejected; authenticated request accepted.' }
    else { Add-Result 'JWT authentication' 'Failed' 'Unauthenticated request was not rejected.' }

    Invoke-AgentJson -Method Get -Path '/api/v1/readers' | Out-Null
    Add-Result 'Readers API' 'Passed'
    Invoke-AgentJson -Method Get -Path '/api/v1/card/status' | Out-Null
    Add-Result 'Card status API' 'Passed'
    Invoke-AgentJson -Method Post -Path '/api/v1/card/atr' -Body @{ readerName = $null } | Out-Null
    Add-Result 'Card ATR API' 'Passed'

    # 20. Interactive card transitions (skippable).
    if ($SkipHardware) {
        Add-Result 'Card polling transitions' 'Not Tested' 'Skipped by -SkipHardware.'
    }
    else {
        Read-Host 'Remove the card, then press Enter'
        Wait-ForCardStatus -ExpectedStatus 'NoCard' -ResultName 'Card polling transitions (removed)'
        Read-Host 'Insert the card, then press Enter'
        Wait-ForCardStatus -ExpectedStatus 'CardPresent' -ResultName 'Card polling transitions (inserted)'
    }

    # 21-22. SSE via Test-SseEvents.ps1 (interactive; skippable).
    if ($SkipHardware) {
        Add-Result 'SSE events' 'Not Tested' 'Skipped by -SkipHardware.'
    }
    else {
        $sseArgs = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Test-SseEvents.ps1'),
            '-BaseUrl', $BaseUrl, '-JwtPublicKeyPath', $script:JwtPublicKeyPath,
            '-JwtPrivateKeyPath', $script:JwtPrivateKeyPath, '-AllowedOrigin', $AllowedOrigin
        )
        if (-not [string]::IsNullOrWhiteSpace($script:JwtToolPath)) {
            $sseArgs += @('-JwtToolPath', $script:JwtToolPath)
        }
        & powershell.exe @sseArgs | Out-Host
        if ($LASTEXITCODE -eq 0) { Add-Result 'SSE events' 'Passed' 'SSE transitions + disconnect/reconnect.' }
        else { Add-Result 'SSE events' 'Failed' 'Test-SseEvents.ps1 reported failure.' }
    }

    # 23. Restart.
    Restart-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
    Invoke-AgentJson -Method Get -Path '/api/v1/readers' | Out-Null
    Add-Result 'Restart health/readers' 'Passed'

    # 24. Reinstall / repair with the SAME package. This is NOT a version upgrade.
    & powershell.exe @installArgs | Out-Host
    if ($LASTEXITCODE -eq 0) { Add-Result 'Reinstall/repair (same package)' 'Passed' 'Same version re-applied.' } else { Add-Result 'Reinstall/repair (same package)' 'Failed' 'Reinstall failed.' }

    # 25. Version upgrade with a DIFFERENT package (-UpgradeZipPath, e.g. 0.1.1-pilot).
    $configDir = Join-Path $ProgramDataPath 'Config'
    $logDir = Join-Path $ProgramDataPath 'Logs'
    $installedManifest = Join-Path $InstallDirectory 'release-manifest.json'
    if ($SkipUpgrade -or [string]::IsNullOrWhiteSpace($UpgradeZipPath)) {
        $skipMsg = if ($SkipUpgrade) { 'Skipped by -SkipUpgrade.' } else { 'Provide -UpgradeZipPath with a different version (e.g. 0.1.1-pilot) for a real version-upgrade test.' }
        Add-Result 'Version upgrade' 'Not Tested' $skipMsg
        Add-Result 'Version upgrade: config/log retention' 'Not Tested' 'Version upgrade not run.'
        Add-Result 'Version upgrade: account/start mode unchanged' 'Not Tested' 'Version upgrade not run.'
    }
    else {
        $versionBefore = Get-InstalledManifestVersion -ManifestPath $installedManifest
        $svcBefore = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue

        # Create GUID-based per-run retention markers with non-secret content and compute pre-upgrade SHA-256
        $script:activeConfigMarker = New-RetentionMarker -TargetDirectory $configDir -Prefix 'phase12-config-retention'
        $script:activeLogMarker = New-RetentionMarker -TargetDirectory $logDir -Prefix 'phase12-log-retention'

        $upgTemp = Join-Path $env:TEMP ("tia-pilot-upg-" + [guid]::NewGuid().ToString('N'))
        [System.IO.Directory]::CreateDirectory($upgTemp) | Out-Null
        try {
            $upgRoot = Expand-ReleasePackage -ReleaseZipPath $UpgradeZipPath -DestinationRoot $upgTemp
            $upgIntegrity = Test-ReleasePackageIntegrity -PackageRoot $upgRoot -RequireSigned:$RequireSigned.IsPresent
            $upgVersion = Get-InstalledManifestVersion -ManifestPath (Join-Path $upgRoot 'release-manifest.json')
            if (-not $upgIntegrity.Ok) {
                Add-Result 'Version upgrade' 'Failed' "Upgrade package integrity failed: $($upgIntegrity.Messages -join ' ')"
            }
            else {
                $upgArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Install-Service.ps1'),
                    '-ServiceName', $ServiceName, '-PackagePath', $upgRoot,
                    '-CertificateThumbprint', $thumbprint, '-CertificateHostName', $CertificateHostName, '-SkipStart')
                if ($RequireSigned) { $upgArgs += '-RequireSigned' }
                & powershell.exe @upgArgs | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    Add-Result 'Version upgrade' 'Failed' 'Upgrade install failed.'
                }
                else {
                    $versionAfter = Get-InstalledManifestVersion -ManifestPath $installedManifest
                    if ($versionAfter -eq $upgVersion -and $versionAfter -ne $versionBefore) {
                        Add-Result 'Version upgrade' 'Passed' "manifest version $versionBefore -> $versionAfter"
                    }
                    else {
                        Add-Result 'Version upgrade' 'Failed' "version before=$versionBefore after=$versionAfter expected=$upgVersion"
                    }

                    Start-Service -Name $ServiceName
                    Start-Sleep -Seconds 3
                    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
                    if (Test-JwtKeysUsable) { Invoke-AgentJson -Method Get -Path '/api/v1/readers' | Out-Null; Add-Result 'Version upgrade: health/readers' 'Passed' } else { Add-Result 'Version upgrade: health/readers' 'Not Tested' 'JWT keys not provided (health only).' }

                    # Verify exact retention markers after upgrade
                    $cfgRetention = Test-RetentionMarker -MarkerPath $script:activeConfigMarker.Path -ExpectedHash $script:activeConfigMarker.Hash -ExpectedParentDirectory $configDir
                    $logRetention = Test-RetentionMarker -MarkerPath $script:activeLogMarker.Path -ExpectedHash $script:activeLogMarker.Hash -ExpectedParentDirectory $logDir
                    if ($cfgRetention.Exists -and $cfgRetention.HashMatch -and $logRetention.Exists -and $logRetention.HashMatch) {
                        Add-Result 'Version upgrade: config/log retention' 'Passed' "Markers intact across upgrade: $($script:activeConfigMarker.FileName), $($script:activeLogMarker.FileName)"
                    }
                    else {
                        Add-Result 'Version upgrade: config/log retention' 'Failed' "Marker retention failed: config(exists=$($cfgRetention.Exists), hashMatch=$($cfgRetention.HashMatch)), log(exists=$($logRetention.Exists), hashMatch=$($logRetention.HashMatch))"
                    }

                    $svcAfter = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
                    if ($svcAfter -and $svcBefore -and $svcAfter.StartName -eq $svcBefore.StartName -and $svcAfter.StartMode -eq $svcBefore.StartMode) { Add-Result 'Version upgrade: account/start mode unchanged' 'Passed' "$($svcAfter.StartName) / $($svcAfter.StartMode)" }
                    else { Add-Result 'Version upgrade: account/start mode unchanged' 'Failed' 'Service identity or start mode changed.' }
                }
            }
        }
        finally {
            if (Test-Path -LiteralPath $upgTemp) { try { [System.IO.Directory]::Delete($upgTemp, $true) } catch { } }
        }
    }

    # 26. Reboot is a real machine event: never reported here. Run -Mode PostReboot after reboot.
    Add-Result 'Reboot continuation' 'Not Tested' 'Reboot the machine, then run this script with -Mode PostReboot to verify persisted state.'

    # 27-28. Uninstall keep data + reinstall.
    if ($SkipUninstallReinstall) {
        Add-Result 'Uninstall keep data' 'Not Tested' 'Skipped by -SkipUninstallReinstall.'
        Add-Result 'Reinstall' 'Not Tested' 'Skipped by -SkipUninstallReinstall.'
    }
    else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Uninstall-Service.ps1') -ServiceName $ServiceName | Out-Host
        $dataKept = Test-Path -LiteralPath $ProgramDataPath
        $markersKept = $true
        if ($script:activeConfigMarker -and $script:activeLogMarker) {
            $cfgUninst = Test-RetentionMarker -MarkerPath $script:activeConfigMarker.Path -ExpectedHash $script:activeConfigMarker.Hash -ExpectedParentDirectory $configDir
            $logUninst = Test-RetentionMarker -MarkerPath $script:activeLogMarker.Path -ExpectedHash $script:activeLogMarker.Hash -ExpectedParentDirectory $logDir
            $markersKept = ($cfgUninst.Exists -and $cfgUninst.HashMatch -and $logUninst.Exists -and $logUninst.HashMatch)
        }
        if ($LASTEXITCODE -eq 0 -and $dataKept -and $markersKept) { Add-Result 'Uninstall keep data' 'Passed' 'ProgramData and markers retained untouched.' } else { Add-Result 'Uninstall keep data' 'Failed' "exit=$LASTEXITCODE dataKept=$dataKept markersKept=$markersKept" }

        & powershell.exe @installArgs | Out-Host
        $markersKeptReinst = $true
        if ($script:activeConfigMarker -and $script:activeLogMarker) {
            $cfgReinst = Test-RetentionMarker -MarkerPath $script:activeConfigMarker.Path -ExpectedHash $script:activeConfigMarker.Hash -ExpectedParentDirectory $configDir
            $logReinst = Test-RetentionMarker -MarkerPath $script:activeLogMarker.Path -ExpectedHash $script:activeLogMarker.Hash -ExpectedParentDirectory $logDir
            $markersKeptReinst = ($cfgReinst.Exists -and $cfgReinst.HashMatch -and $logReinst.Exists -and $logReinst.HashMatch)
        }
        if ($LASTEXITCODE -eq 0 -and $markersKeptReinst) { Add-Result 'Reinstall' 'Passed' 'Service reinstalled; markers untouched.' } else { Add-Result 'Reinstall' 'Failed' "Reinstall failed or markers modified. exit=$LASTEXITCODE markersKept=$markersKeptReinst" }
    }
}

# ---- Preflight ----------------------------------------------------------------------
Add-Result 'PowerShell version' 'Passed' ("Windows PowerShell " + $PSVersionTable.PSVersion.ToString())

$needsAdmin = ($Mode -eq 'Full')
if ($needsAdmin -and -not $WhatIfPreference) {
    if (Test-IsAdministrator) { Add-Result 'Administrator' 'Passed' }
    else { Add-Result 'Administrator' 'Failed' 'Administrator rights are required for Full mode.'; Complete-Acceptance }
}
else {
    $adminMsg = if ($WhatIfPreference) { 'WhatIf mode: not enforced.' } else { "Mode=$Mode does not require Administrator." }
    Add-Result 'Administrator' 'Not Tested' $adminMsg
}

if (-not (Test-Path -LiteralPath $ReleaseZipPath -PathType Leaf)) {
    Add-Result 'Release ZIP present' 'Failed' "Not found: $ReleaseZipPath"
    Complete-Acceptance
}
$resolvedZip = (Resolve-Path -LiteralPath $ReleaseZipPath).Path
$zipHashBefore = Get-FileSha256 -Path $resolvedZip
Add-Result 'Release ZIP present' 'Passed' (Split-Path -Leaf $resolvedZip)

# Temp scratch is created with the .NET API so it is never suppressed by -WhatIf and is always
# cleaned up; -WhatIf only gates the real install/service operations.
$tempRoot = Join-Path $env:TEMP ("tia-pilot-" + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null

try {
    # ---- Extract + integrity (all modes) -------------------------------------------
    $packageRoot = Expand-ReleasePackage -ReleaseZipPath $resolvedZip -DestinationRoot $tempRoot
    Add-Result 'Extract package' 'Passed' 'Extracted to a temporary directory.'

    $integrity = Test-ReleasePackageIntegrity -PackageRoot $packageRoot -RequireSigned:$RequireSigned.IsPresent

    if ($integrity.ManifestPresent) { Add-Result 'release-manifest.json' 'Passed' "signingStatus=$($integrity.SigningStatus)" }
    else { Add-Result 'release-manifest.json' 'Failed' ($integrity.Messages -join ' ') }

    if ($integrity.ChecksumOk) { Add-Result 'SHA-256 checksum' 'Passed' }
    else { Add-Result 'SHA-256 checksum' 'Failed' ($integrity.Messages -join ' ') }

    if ($integrity.SecretViolations.Count -eq 0) { Add-Result 'Secret exclusion' 'Passed' 'No PFX/key/JWT/.env.local/logs/PII in payload.' }
    else { Add-Result 'Secret exclusion' 'Failed' "Found: $($integrity.SecretViolations -join ', ')" }

    if ($integrity.SigningStatus -eq 'Signed') {
        Add-Result 'Signing status' 'Passed' 'Signed package.'
    }
    elseif ($RequireSigned) {
        Add-Result 'Signing status' 'Failed' 'RequireSigned but package is not Signed.'
    }
    else {
        Add-Result 'Signing status' 'Passed' 'UnsignedPilot accepted (WARNING: unsigned; SmartScreen/unknown-publisher warnings apply).'
        Write-Warning 'This is an UNSIGNED PILOT package. Do not use for production distribution.'
    }

    # ---- Mode-specific behavior ----------------------------------------------------
    switch ($Mode) {
        'VerifyOnly' {
            # Integrity already reported above.
        }

        'Tamper' {
            # Flip one byte of the service executable in the EXTRACTED COPY only.
            $exe = Join-Path $packageRoot 'app\ThaiIdCardAgent.Service.exe'
            if (-not (Test-Path -LiteralPath $exe)) {
                Add-Result 'Tamper detection' 'Failed' 'Service executable not found in package.'
            }
            else {
                $bytes = [System.IO.File]::ReadAllBytes($exe)
                $index = [int]($bytes.Length * 0.25)
                $bytes[$index] = [byte]($bytes[$index] -bxor 0xFF)
                [System.IO.File]::WriteAllBytes($exe, $bytes)

                $after = Test-ReleasePackageIntegrity -PackageRoot $packageRoot -RequireSigned:$RequireSigned.IsPresent
                if (-not $after.ChecksumOk) { Add-Result 'Tamper detection' 'Passed' 'Modified package rejected before install.' }
                else { Add-Result 'Tamper detection' 'Failed' 'Tampered package was not detected.' }
            }

            $zipHashAfter = Get-FileSha256 -Path $resolvedZip
            if ($zipHashAfter -eq $zipHashBefore) { Add-Result 'Original ZIP unmodified' 'Passed' }
            else { Add-Result 'Original ZIP unmodified' 'Failed' 'Source ZIP hash changed.' }

            Add-Result 'Existing service unchanged' 'Passed' 'Tamper mode never installs or stops the service.'
        }

        'Rollback' {
            # Simulate upgrade failures against a temporary install directory.
            $simInstall = Join-Path $tempRoot 'install'
            $simData = Join-Path $tempRoot 'programdata'
            $simConfig = Join-Path $simData 'Config'
            $simLogs = Join-Path $simData 'Logs'
            New-Item -ItemType Directory -Force -Path $simInstall, $simConfig, $simLogs | Out-Null
            Set-Content -Path (Join-Path $simInstall 'ThaiIdCardAgent.Service.exe') -Value 'OLD-VERSION' -NoNewline
            Set-Content -Path (Join-Path $simConfig 'appsettings.Production.json') -Value '{"keep":true}' -NoNewline
            Set-Content -Path (Join-Path $simLogs 'agent.log') -Value 'existing log' -NoNewline

            # Create GUID retention markers before rollback
            $simConfigMarker = New-RetentionMarker -TargetDirectory $simConfig -Prefix 'phase12-config-retention'
            $simLogMarker = New-RetentionMarker -TargetDirectory $simLogs -Prefix 'phase12-log-retention'
            $source = Join-Path $packageRoot 'app'

            # (a) copy failure -> rollback restores previous binary
            try {
                Copy-ReleasePayloadWithRollback -SourceDir $source -DestinationDir $simInstall -BackupRoot $simData -SimulateFailure | Out-Null
                Add-Result 'Rollback on copy failure' 'Failed' 'Simulated failure did not throw.'
            }
            catch {
                $exeContent = Get-Content -LiteralPath (Join-Path $simInstall 'ThaiIdCardAgent.Service.exe') -Raw
                if ($exeContent -eq 'OLD-VERSION') { Add-Result 'Rollback on copy failure' 'Passed' 'Previous binary restored.' }
                else { Add-Result 'Rollback on copy failure' 'Failed' 'Previous binary not restored.' }
            }

            # (b) config/log retention
            $configKept = Test-Path -LiteralPath (Join-Path $simConfig 'appsettings.Production.json')
            $logKept = Test-Path -LiteralPath (Join-Path $simLogs 'agent.log')
            $cfgRes = Test-RetentionMarker -MarkerPath $simConfigMarker.Path -ExpectedHash $simConfigMarker.Hash -ExpectedParentDirectory $simConfig
            $logRes = Test-RetentionMarker -MarkerPath $simLogMarker.Path -ExpectedHash $simLogMarker.Hash -ExpectedParentDirectory $simLogs
            if ($configKept -and $logKept -and $cfgRes.Exists -and $cfgRes.HashMatch -and $logRes.Exists -and $logRes.HashMatch) {
                Add-Result 'Config/log retention' 'Passed' 'Config and logs untouched by rollback (SHA-256 match).'
            }
            else {
                Add-Result 'Config/log retention' 'Failed' "configKept=$configKept logKept=$logKept markerCfg=$($cfgRes.HashMatch) markerLog=$($logRes.HashMatch)"
            }

            # Clean up only the exact simulated markers
            Remove-RetentionMarker -MarkerPath $simConfigMarker.Path -ExpectedParentDirectory $simConfig
            Remove-RetentionMarker -MarkerPath $simLogMarker.Path -ExpectedParentDirectory $simLogs

            # (c) invalid manifest / checksum mismatch rejected before install
            $badPkg = Join-Path $tempRoot 'badpkg'
            New-Item -ItemType Directory -Force -Path (Join-Path $badPkg 'app') | Out-Null
            Set-Content -Path (Join-Path $badPkg 'app\ThaiIdCardAgent.Service.exe') -Value 'payload' -NoNewline
            Set-Content -Path (Join-Path $badPkg 'release-manifest.json') -Value 'not-json'
            New-ReleaseChecksumManifest -PackageRoot $badPkg | Out-Null
            Set-Content -Path (Join-Path $badPkg 'app\ThaiIdCardAgent.Service.exe') -Value 'tampered-after-manifest' -NoNewline
            $bad = Test-ReleasePackageIntegrity -PackageRoot $badPkg
            if (-not $bad.Ok) { Add-Result 'Invalid manifest / checksum mismatch rejected' 'Passed' }
            else { Add-Result 'Invalid manifest / checksum mismatch rejected' 'Failed' 'Bad package accepted.' }

            Add-Result 'Service start failure rollback' 'Not Tested' 'Requires a real service and explicit confirmation; run Full mode on a pilot machine.'
        }

        'PostReboot' {
            Invoke-PostRebootChecks
        }

        'Full' {
            Invoke-FullAcceptance -PackageRoot $packageRoot
        }
    }
}
catch {
    Add-Result 'Acceptance run' 'Failed' $_.Exception.Message
}
finally {
    if ($script:activeConfigMarker) {
        try {
            $cDir = Join-Path $ProgramDataPath 'Config'
            Remove-RetentionMarker -MarkerPath $script:activeConfigMarker.Path -ExpectedParentDirectory $cDir
        }
        catch { }
        finally { $script:activeConfigMarker = $null }
    }
    if ($script:activeLogMarker) {
        try {
            $lDir = Join-Path $ProgramDataPath 'Logs'
            Remove-RetentionMarker -MarkerPath $script:activeLogMarker.Path -ExpectedParentDirectory $lDir
        }
        catch { }
        finally { $script:activeLogMarker = $null }
    }

    if ($KeepExtractedPackage) {
        Write-Host "Extracted package kept at: $tempRoot"
    }
    elseif (Test-Path -LiteralPath $tempRoot) {
        # .NET delete so temp scratch is always removed, even under -WhatIf.
        try { [System.IO.Directory]::Delete($tempRoot, $true) } catch { }
    }
}

Complete-Acceptance

