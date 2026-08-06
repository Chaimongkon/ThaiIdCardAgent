#requires -Version 5.1
<#
.SYNOPSIS
    Collects read-only, sanitized diagnostics for a ThaiIdCardAgent pilot machine.

.DESCRIPTION
    Gathers service, port, certificate, Smart Card, reader, health, and release-manifest
    information for support tickets. Output is sanitized: it never includes JWTs, private
    keys, certificate passwords, Authorization headers, or cardholder/personal data.

    Read-only: it does not start/stop services, modify certificates, or change configuration.

.NOTES
    Windows PowerShell 5.1 compatible. Use -AsJson to emit a machine-readable object suitable
    for attaching to a support ticket (still sanitized).
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$BaseUrl = 'https://localhost:18443',
    [int]$Port = 18443,
    [string]$InstallDirectory = 'C:\Program Files\ThaiIdCardAgent',
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ServiceInfo {
    param([string]$Name)
    $info = [ordered]@{
        Name      = $Name
        Installed = $false
        State     = 'NotInstalled'
        StartMode = $null
        Account   = $null
        Pid       = $null
        BinaryPath = $null
        DelayedAutoStart = $null
    }
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    if ($svc) {
        $info.Installed = $true
        $info.State = $svc.State
        $info.StartMode = $svc.StartMode
        $info.Account = $svc.StartName
        $info.Pid = if ($svc.ProcessId -gt 0) { $svc.ProcessId } else { $null }
        $info.BinaryPath = $svc.PathName
        try {
            $delayed = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name 'DelayedAutostart' -ErrorAction SilentlyContinue).DelayedAutostart
            $info.DelayedAutoStart = [bool]$delayed
        }
        catch { $info.DelayedAutoStart = $null }
    }
    return [pscustomobject]$info
}

function Get-PortListenerPid {
    param([int]$PortNumber)
    try {
        $conn = Get-NetTCPConnection -State Listen -LocalPort $PortNumber -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($conn) { return $conn.OwningProcess }
    }
    catch { }
    return $null
}

function Get-ExecutableInfo {
    param([string]$Directory)
    $exe = Join-Path $Directory 'ThaiIdCardAgent.Service.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        return [pscustomobject]@{ Path = $null; Version = $null; Present = $false }
    }
    $version = $null
    try { $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { }
    return [pscustomobject]@{ Path = $exe; Version = $version; Present = $true }
}

function Get-CertificateInfo {
    $result = [pscustomobject]@{
        Subject          = $null
        Thumbprint       = $null
        NotAfterUtc      = $null
        HasPrivateKey    = $false
        PrivateKeyUsable = $false
        Present          = $false
    }
    $thumbprint = $env:Agent__Https__Certificate__Thumbprint
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $cert = $null
        if (-not [string]::IsNullOrWhiteSpace($thumbprint)) {
            $normalized = ($thumbprint -replace '\s', '').ToUpperInvariant()
            $cert = $store.Certificates | Where-Object { ($_.Thumbprint -replace '\s', '').ToUpperInvariant() -eq $normalized } | Select-Object -First 1
        }
        if (-not $cert) {
            $cert = $store.Certificates | Where-Object { $_.Subject -eq 'CN=localhost' } | Sort-Object NotAfter -Descending | Select-Object -First 1
        }
        if ($cert) {
            $result.Subject = $cert.Subject
            $result.Thumbprint = $cert.Thumbprint
            $result.NotAfterUtc = $cert.NotAfter.ToUniversalTime().ToString('o')
            $result.HasPrivateKey = $cert.HasPrivateKey
            $result.Present = $true
            try {
                $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
                $result.PrivateKeyUsable = ($null -ne $rsa)
            }
            catch { $result.PrivateKeyUsable = $false }
        }
    }
    finally { $store.Dispose() }
    return $result
}

function Get-SmartCardServiceState {
    $svc = Get-Service -Name 'SCardSvr' -ErrorAction SilentlyContinue
    if ($svc) { return [string]$svc.Status }
    return 'NotFound'
}

function Get-AgentHealthInfo {
    param([string]$Uri)
    try {
        $health = Invoke-RestMethod -Uri "$Uri/api/v1/health" -Method Get -TimeoutSec 10
        return [pscustomobject]@{ Reachable = $true; Status = [string]$health.status; Version = [string]$health.version }
    }
    catch {
        return [pscustomobject]@{ Reachable = $false; Status = $null; Version = $null }
    }
}

function Get-ReaderNames {
    # Read-only reader enumeration via WinSCard, if available. Returns names only (no card data).
    try {
        Add-Type -AssemblyName System.Runtime.InteropServices -ErrorAction SilentlyContinue | Out-Null
    }
    catch { }
    return @()  # Reader enumeration is reported by the service health/readers API; kept empty here to avoid PC/SC coupling.
}

function Get-ReleaseManifestInfo {
    param([string]$Directory)
    $manifest = Join-Path $Directory 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifest)) {
        return [pscustomobject]@{ Present = $false; Version = $null; GitCommit = $null; SigningStatus = $null }
    }
    try {
        $m = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        return [pscustomobject]@{
            Present       = $true
            Version       = [string]$m.version
            GitCommit     = [string]$m.gitCommit
            SigningStatus = [string]$m.signingStatus
        }
    }
    catch {
        return [pscustomobject]@{ Present = $true; Version = $null; GitCommit = $null; SigningStatus = 'Unreadable' }
    }
}

$diagnostics = [ordered]@{
    generatedAtUtc  = ([datetime]::UtcNow).ToString('o')
    machine         = $env:COMPUTERNAME
    service         = Get-ServiceInfo -Name $ServiceName
    portListenerPid = Get-PortListenerPid -PortNumber $Port
    executable      = Get-ExecutableInfo -Directory $InstallDirectory
    certificate     = Get-CertificateInfo
    smartCardService = Get-SmartCardServiceState
    health          = Get-AgentHealthInfo -Uri $BaseUrl
    releaseManifest = Get-ReleaseManifestInfo -Directory $InstallDirectory
}

# Explicit guarantee: no secrets or PII are collected above. Do not add JWT/private key/
# Authorization header/cardholder fields to this object.

if ($AsJson) {
    ([pscustomobject]$diagnostics) | ConvertTo-Json -Depth 6
}
else {
    Write-Host "ThaiIdCardAgent diagnostics (sanitized) - $($diagnostics.generatedAtUtc)"
    Write-Host "Machine            : $($diagnostics.machine)"
    Write-Host "Service installed  : $($diagnostics.service.Installed)"
    Write-Host "Service state      : $($diagnostics.service.State)"
    Write-Host "Service account    : $($diagnostics.service.Account)"
    Write-Host "Service start mode : $($diagnostics.service.StartMode) (delayed=$($diagnostics.service.DelayedAutoStart))"
    Write-Host "Service PID        : $($diagnostics.service.Pid)"
    Write-Host "Port $Port listener PID : $($diagnostics.portListenerPid)"
    Write-Host "Executable         : $($diagnostics.executable.Path) (version=$($diagnostics.executable.Version))"
    Write-Host "Certificate subject: $($diagnostics.certificate.Subject)"
    Write-Host "Certificate thumb  : $($diagnostics.certificate.Thumbprint)"
    Write-Host "Certificate expiry : $($diagnostics.certificate.NotAfterUtc)"
    Write-Host "Cert private key   : hasKey=$($diagnostics.certificate.HasPrivateKey) usable=$($diagnostics.certificate.PrivateKeyUsable)"
    Write-Host "SmartCard service  : $($diagnostics.smartCardService)"
    Write-Host "Health reachable   : $($diagnostics.health.Reachable) (status=$($diagnostics.health.Status) version=$($diagnostics.health.Version))"
    Write-Host "Release version    : $($diagnostics.releaseManifest.Version)"
    Write-Host "Release commit     : $($diagnostics.releaseManifest.GitCommit)"
    Write-Host "Signing status     : $($diagnostics.releaseManifest.SigningStatus)"
}
