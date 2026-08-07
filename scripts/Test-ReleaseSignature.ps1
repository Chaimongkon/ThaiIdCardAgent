#requires -Version 5.1
<#
.SYNOPSIS
    Verifies the integrity and Authenticode signatures of a ThaiIdCardAgent release package.

.DESCRIPTION
    Verification order (fail closed at every step):

      1. SHA-256 checksum manifest.
      2. Signing allowlist: every required file present, no unexpected executable content.
      3. Authenticode signature of each allowlisted file: signed, not tampered, correct signer,
         SHA-256 digest, timestamped.
      4. Declared signingStatus in release-manifest.json consistent with what was measured.

    -RequireSigned turns an unsigned or partially signed package into a failure. Without it, an
    UnsignedPilot package is reported with a warning and a success exit code so pilot verification
    can proceed. -RequireTimestamp and -RequireRfc3161Timestamp add timestamp requirements;
    -ExpectedSignerThumbprint / -ExpectedSignerSubject reject a package signed by anyone else.

.NOTES
    Windows PowerShell 5.1 compatible. Non-zero exit on failure so callers can gate on it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [switch]$RequireSigned,
    [switch]$RequireTimestamp,
    [switch]$RequireRfc3161Timestamp,
    [switch]$RequireTrustedChain,
    [string]$ExpectedSignerThumbprint,
    [string]$ExpectedSignerSubject,
    [string]$AllowlistPath,
    [ValidateSet('SHA256', 'SHA384', 'SHA512')][string]$RequiredDigestAlgorithm = 'SHA256'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$payloadDir = Join-Path $packageRoot 'app'
$metadataPath = Join-Path $packageRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $payloadDir)) { throw "Package payload folder was not found: $payloadDir" }
if (-not (Test-Path -LiteralPath $metadataPath)) { throw "release-manifest.json was not found: $metadataPath" }

# 1. Integrity: checksum manifest must verify.
$checksum = Test-ReleaseChecksum -PackageRoot $packageRoot
if (-not $checksum.Ok) {
    throw "Checksum verification FAILED. Missing=$($checksum.Missing -join ',') Modified=$($checksum.Modified -join ',') Extra=$($checksum.Extra -join ',')"
}
Write-Host 'Checksum verification: OK'

# 2. Metadata.
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$signingStatus = [string]$metadata.signingStatus
Write-Host "Declared signing status: $signingStatus"

# 3. Signing allowlist and per-file signature status.
$policy = if ([string]::IsNullOrWhiteSpace($AllowlistPath)) { Get-ReleaseSigningPolicy } else { Get-ReleaseSigningPolicy -PolicyPath $AllowlistPath }
Write-Host "Signing allowlist: $($policy.PolicyPath)"

# Timestamp and signer requirements only apply when signatures are being demanded; an unsigned
# pilot package is measured, reported, and gated separately below.
$report = New-ReleaseSigningReport -PackageRoot $packageRoot -Policy $policy `
    -ExpectedThumbprint $ExpectedSignerThumbprint -ExpectedSubject $ExpectedSignerSubject `
    -RequiredDigestAlgorithm $RequiredDigestAlgorithm `
    -RequireTimestamp:($RequireSigned -and $RequireTimestamp) `
    -RequireRfc3161Timestamp:($RequireSigned -and $RequireRfc3161Timestamp) `
    -RequireTrustedChain:$RequireTrustedChain

$tampered = 0
$unsigned = 0
$signed = 0
foreach ($file in $report.Files) {
    if ($file.Tampered) {
        $tampered++
        Write-Host ("INVALID  : {0} [HashMismatch] file modified after signing" -f $file.RelativePath)
    }
    elseif (-not $file.Signed) {
        $unsigned++
        Write-Host "NotSigned: $($file.RelativePath)"
    }
    else {
        $signed++
        $ts = if ($file.Timestamped) { "$($file.TimestampKind) timestamp" } else { 'no timestamp' }
        $trust = if ($file.Status -eq 'Valid') { 'trusted chain' } else { "untrusted chain here: $($file.Status)" }
        Write-Host ("Signed   : {0} [{1}] ({2}; {3})" -f $file.RelativePath, $file.DigestAlgorithm, $ts, $trust)
    }
}

Write-Host ''
Write-Host "Allowlist targets: $($report.SignTargetCount)  Required: $($report.RequiredFileCount)  Signed: $signed  NotSigned: $unsigned  Invalid: $tampered"

# 4. Gates.
if ($report.UnexpectedExecutables.Count -gt 0) {
    throw "Package contains executable content that is not in the signing allowlist: $($report.UnexpectedExecutables -join ', '). Package REJECTED."
}
if ($report.MissingRequired.Count -gt 0) {
    throw "Package is missing required signed file(s): $($report.MissingRequired -join ', '). Package REJECTED."
}
if ($tampered -gt 0) {
    throw "$tampered file(s) have an invalid signature. Package REJECTED."
}

if ($RequireSigned) {
    if ($signingStatus -ne 'Signed') {
        throw "RequireSigned: package signing status is '$signingStatus', not 'Signed'. REJECTED."
    }
    if (-not $report.Ok) {
        throw ("RequireSigned: signature verification FAILED. REJECTED.`n  " + ($report.Messages -join "`n  "))
    }
    Write-Host "Signer subject   : $($report.SignerSubject)"
    Write-Host "Signer thumbprint: $($report.CertificateThumbprint)"
    Write-Host "Signature digest : $($report.SignatureAlgorithm)"
    Write-Host "Timestamp        : $($report.TimestampKind)"
    Write-Host 'RequireSigned: PASSED.'
    return
}

if ($signingStatus -ne 'Signed' -or $unsigned -gt 0) {
    Write-Warning 'Package is an UNSIGNED PILOT build. Do not use for production distribution.'
}
elseif (-not $report.Ok) {
    Write-Warning ("Package declares Signed but signature verification reported problems:`n  " + ($report.Messages -join "`n  "))
}
Write-Host 'Verification PASSED.'
