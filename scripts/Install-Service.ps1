#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$DisplayName = 'Thai ID Card Local Agent',
    [string]$Description = 'Local service for connecting authorized web applications to a PC/SC smart card reader.',
    [string]$ProgramPath = 'C:\Program Files\ThaiIdCardAgent',
    [string]$ProgramDataPath = 'C:\ProgramData\ThaiIdCardAgent',
    [string]$PublishPath,
    [string]$HealthUri = 'https://localhost:18443/api/v1/health',
    [string]$CertificateThumbprint,
    [string]$CertificateSubjectName = 'localhost',
    [string]$CertificateHostName = 'localhost',
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService',
    [string]$PackagePath,
    [switch]$RequireSigned,
    [switch]$SkipStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

function Test-IsAdministrator {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Normalize-Thumbprint([string]$Thumbprint) {
    return ($Thumbprint -replace '\s', '').ToUpperInvariant()
}

function Get-HttpsCertificate {
    param([string]$Thumbprint, [string]$SubjectName)
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    try {
        if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
            $normalizedThumbprint = Normalize-Thumbprint $Thumbprint
            return $store.Certificates |
                Where-Object { (Normalize-Thumbprint $_.Thumbprint) -eq $normalizedThumbprint } |
                Sort-Object NotAfter -Descending |
                Select-Object -First 1
        }

        return $store.Certificates |
            Where-Object { $_.Subject -eq "CN=$SubjectName" } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
    }
    finally {
        $store.Dispose()
    }
}

function Test-CertificateSan {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, [string]$HostName)
    $san = ($Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.17' } |
        Select-Object -First 1).Format($false)
    return $san -match "DNS Name=$([regex]::Escape($HostName))(,|`$)"
}

function Test-CertificateServerAuthenticationEku {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $eku = $Certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } | Select-Object -First 1
    if (-not $eku) { return $true }
    return [bool]($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.1' })
}

function Test-LocalMachineTrust {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new($true)
    try {
        return $chain.Build($Certificate)
    }
    finally {
        $chain.Dispose()
    }
}

function Get-PrivateKeyPath {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($rsa -is [System.Security.Cryptography.RSACng] -and $rsa.Key.UniqueName) {
        $path = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        if (Test-Path -LiteralPath $path) { return $path }
    }

    if ($Certificate.PrivateKey -is [System.Security.Cryptography.RSACryptoServiceProvider] -and $Certificate.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName) {
        $path = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($Certificate.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName)"
        if (Test-Path -LiteralPath $path) { return $path }
    }

    return $null
}

function Test-PrivateKeyAcl {
    param([string]$KeyPath, [string]$Account)
    $acl = Get-Acl -LiteralPath $KeyPath
    return [bool]($acl.Access | Where-Object {
        $_.IdentityReference.Value -eq $Account -and
        $_.AccessControlType -eq 'Allow' -and
        ($_.FileSystemRights.ToString().Contains('Read') -or $_.FileSystemRights.ToString().Contains('FullControl'))
    })
}

if (-not (Test-IsAdministrator) -and -not $WhatIfPreference) {
    throw 'Administrator rights are required.'
}

$scriptDir = $PSScriptRoot
$rootCandidate = (Join-Path $scriptDir '..')
$root = if (Test-Path -LiteralPath (Join-Path $rootCandidate 'artifacts')) { (Resolve-Path -LiteralPath $rootCandidate).Path } else { $scriptDir }
$PublishPath = if ([string]::IsNullOrWhiteSpace($PublishPath)) { Join-Path $root 'artifacts\publish\win-x64' } else { $PublishPath }
$resolvedPublishPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishPath)
$resolvedProgramPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramPath)
$resolvedProgramDataPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProgramDataPath)
$configPath = Join-Path $resolvedProgramDataPath 'Config'
$logPath = Join-Path $resolvedProgramDataPath 'Logs'
$exePath = Join-Path $resolvedProgramPath 'ThaiIdCardAgent.Service.exe'

# Source resolution:
#   -PackagePath  -> a verified release package; copy from <PackagePath>\app after integrity checks.
#   otherwise     -> legacy flat publish output (existing, proven behavior).
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $resolvedPackagePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackagePath)
    $sourceDir = Join-Path $resolvedPackagePath 'app'
    if (-not (Test-Path -LiteralPath $sourceDir)) {
        throw "Release package payload folder was not found: $sourceDir"
    }

    Write-Host "Verifying release package integrity: $resolvedPackagePath"
    $checksum = Test-ReleaseChecksum -PackageRoot $resolvedPackagePath
    if (-not $checksum.Ok) {
        throw "Refusing to install: checksum verification failed. Missing=$($checksum.Missing -join ',') Modified=$($checksum.Modified -join ',') Extra=$($checksum.Extra -join ',')"
    }
    Write-Host 'Checksum verification: OK'

    if ($RequireSigned) {
        & (Join-Path $PSScriptRoot 'Test-ReleaseSignature.ps1') -PackagePath $resolvedPackagePath -RequireSigned
        Write-Host 'Signature verification (RequireSigned): OK'
    }
}
else {
    if ($RequireSigned) {
        throw '-RequireSigned requires -PackagePath (a signed release package with a manifest).'
    }
    $sourceDir = $resolvedPublishPath
}

$sourceExe = Join-Path $sourceDir 'ThaiIdCardAgent.Service.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Service executable was not found. Run scripts\Publish-WinX64.ps1 (or point -PackagePath at a release package): $sourceExe"
}

$certificate = Get-HttpsCertificate -Thumbprint $CertificateThumbprint -SubjectName $CertificateSubjectName
if (-not $certificate) {
    throw "HTTPS certificate was not found in Cert:\LocalMachine\My. Thumbprint='$CertificateThumbprint' Subject='CN=$CertificateSubjectName'."
}

if (-not $certificate.HasPrivateKey) { throw 'HTTPS certificate HasPrivateKey is False.' }
if (-not (Test-CertificateSan -Certificate $certificate -HostName $CertificateHostName)) { throw "HTTPS certificate SAN does not include DNS Name=$CertificateHostName." }
if (-not (Test-CertificateServerAuthenticationEku -Certificate $certificate)) { throw 'HTTPS certificate EKU does not include Server Authentication.' }
if (-not (Test-LocalMachineTrust -Certificate $certificate)) {
    throw "HTTPS certificate chain is not trusted in LocalMachine context. Export the public certificate and run: certutil -addstore Root <public-certificate.cer>"
}

$aclAccount = if ($ServiceAccount -eq 'NT AUTHORITY\LocalService') { 'NT AUTHORITY\LOCAL SERVICE' } else { $ServiceAccount }
$keyPath = Get-PrivateKeyPath -Certificate $certificate
if (-not $keyPath) { throw 'Unable to resolve HTTPS certificate private key file path.' }
if (-not (Test-PrivateKeyAcl -KeyPath $keyPath -Account $aclAccount)) {
    throw "Service account '$aclAccount' does not have private-key read access. Run scripts\Set-CertificatePrivateKeyAcl.ps1 -Thumbprint '$($certificate.Thumbprint)' -Account '$aclAccount'"
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

    # Rollback-protected copy: snapshots the existing binaries and restores them if the
    # copy fails, so a partial copy never replaces a working install. Config/logs under
    # ProgramData are untouched.
    try {
        Copy-ReleasePayloadWithRollback -SourceDir $sourceDir -DestinationDir $resolvedProgramPath -BackupRoot $resolvedProgramDataPath | Out-Null
    }
    catch {
        throw "Failed to copy payload to $resolvedProgramPath. Previous install restored. Error: $($_.Exception.Message)"
    }

    # When installing from a release package, record its manifest in the install directory so the
    # installed version/commit/signing status is traceable (used by Get-AgentDiagnostics and
    # upgrade verification). This is metadata only; it is never executed.
    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
        $sourceManifest = Join-Path $resolvedPackagePath 'release-manifest.json'
        if (Test-Path -LiteralPath $sourceManifest) {
            Copy-Item -LiteralPath $sourceManifest -Destination (Join-Path $resolvedProgramPath 'release-manifest.json') -Force
        }
    }

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
        Invoke-RestMethod -Uri $HealthUri -Method Get -TimeoutSec 10 | Out-Null
    }

    Write-Host "Installed or upgraded service: $ServiceName"
}
