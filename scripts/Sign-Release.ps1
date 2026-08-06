#requires -Version 5.1
<#
.SYNOPSIS
    Applies Authenticode code signatures to a ThaiIdCardAgent release package.

.DESCRIPTION
    Signs the service executable and the project's own assemblies (and, with
    -IncludeScripts, the PowerShell scripts inside the package) using a Code Signing
    certificate taken either from the Windows certificate store (by thumbprint) or from a
    PFX file whose password is supplied as a SecureString.

    Fails closed: a certificate without the Code Signing EKU, or an expired / not-yet-valid
    certificate, stops the run. When a timestamp server is supplied and timestamping fails,
    the run is NOT reported as Passed. A localhost/HTTPS certificate can never be used
    because it lacks the Code Signing EKU.

    -Unsigned produces an explicit unsigned pilot result (loud warning, no signatures) so
    pilot deployments can proceed without a production certificate.

.NOTES
    Windows PowerShell 5.1 compatible. Supports -WhatIf. Never writes the PFX password to
    output, logs, or the release manifest.
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,

    [Parameter(ParameterSetName = 'Store')][string]$CertificateThumbprint,
    [Parameter(ParameterSetName = 'Store')][ValidateSet('CurrentUser', 'LocalMachine')][string]$StoreLocation = 'CurrentUser',

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)][string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')][System.Security.SecureString]$PfxPassword,

    [Parameter(ParameterSetName = 'Unsigned', Mandatory = $true)][switch]$Unsigned,

    [string]$TimestampServer,
    [ValidateSet('SHA256', 'SHA384', 'SHA512')][string]$HashAlgorithm = 'SHA256',
    [switch]$IncludeScripts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$payloadDir = Join-Path $packageRoot 'app'
$metadataPath = Join-Path $packageRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $payloadDir)) { throw "Package payload folder was not found: $payloadDir" }
if (-not (Test-Path -LiteralPath $metadataPath)) { throw "release-manifest.json was not found: $metadataPath" }

function Get-SignTargetFile {
    param([string]$PayloadDir, [bool]$IncludeScriptFiles)
    $targets = @()
    $targets += Get-ChildItem -LiteralPath $PayloadDir -Recurse -File -Force |
        Where-Object { $_.Extension -ieq '.exe' -or ($_.Extension -ieq '.dll' -and $_.Name -like 'ThaiIdCardAgent.*') }
    if ($IncludeScriptFiles) {
        $targets += Get-ChildItem -LiteralPath $PayloadDir -Recurse -File -Force |
            Where-Object { $_.Extension -ieq '.ps1' -or $_.Extension -ieq '.psm1' -or $_.Extension -ieq '.psd1' }
    }
    return $targets | Sort-Object -Property FullName -Unique
}

# ---- Unsigned pilot mode -------------------------------------------------------------
if ($Unsigned) {
    Write-Warning 'UNSIGNED PILOT MODE: no Authenticode signatures will be applied.'
    Write-Warning 'Unsigned binaries will trigger SmartScreen / publisher-unknown warnings and are for pilot use only.'
    $check = Test-ReleaseChecksum -PackageRoot $packageRoot
    if (-not $check.Ok) {
        throw "Refusing to mark unsigned pilot: checksum verification failed. Modified=$($check.Modified -join ',') Missing=$($check.Missing -join ',') Extra=$($check.Extra -join ',')"
    }
    Write-Host 'Unsigned pilot package verified (checksums OK). signingStatus remains UnsignedPilot.'
    return
}

# ---- Resolve the signing certificate -------------------------------------------------
$certificate = $null
if ($PSCmdlet.ParameterSetName -eq 'Pfx') {
    if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX file was not found: $PfxPath" }
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    if ($null -eq $PfxPassword) { $PfxPassword = [System.Security.SecureString]::new() }
    # SecureString password is passed straight to the constructor; never converted to text.
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $PfxPassword, $flags)
}
else {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw 'Specify -CertificateThumbprint (store), -PfxPath (file), or -Unsigned (pilot).'
    }
    $normalized = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    $storePath = "Cert:\$StoreLocation\My\$normalized"
    if (-not (Test-Path -LiteralPath $storePath)) {
        throw "Code signing certificate not found in $StoreLocation\My for the supplied thumbprint."
    }
    $certificate = Get-Item -LiteralPath $storePath
}

# ---- Validate certificate (fail closed) ---------------------------------------------
Test-CodeSigningCertificate -Certificate $certificate | Out-Null
Write-Host "Signing certificate subject   : $($certificate.Subject)"
Write-Host "Signing certificate thumbprint: $($certificate.Thumbprint)"
Write-Host "Certificate valid until       : $($certificate.NotAfter.ToUniversalTime().ToString('o'))"

if ([string]::IsNullOrWhiteSpace($TimestampServer)) {
    Write-Warning 'No -TimestampServer supplied: signatures will become invalid when the certificate expires. Supply a timestamp server for production.'
}

$targets = @(Get-SignTargetFile -PayloadDir $payloadDir -IncludeScriptFiles:$IncludeScripts.IsPresent)
if ($targets.Count -eq 0) { throw 'No signable files were found in the package payload.' }

if (-not $PSCmdlet.ShouldProcess($packageRoot, "Sign $($targets.Count) file(s) and update release manifest")) {
    Write-Host 'WhatIf: no files were signed and the manifest was not modified.'
    foreach ($t in $targets) { Write-Host "WhatIf would sign: $(Get-ReleaseRelativePath -Root $packageRoot -FullName $t.FullName)" }
    return
}

# ---- Sign and verify each target -----------------------------------------------------
foreach ($file in $targets) {
    $rel = Get-ReleaseRelativePath -Root $packageRoot -FullName $file.FullName
    $signArgs = @{
        FilePath      = $file.FullName
        Certificate   = $certificate
        HashAlgorithm = $HashAlgorithm
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) { $signArgs['TimestampServer'] = $TimestampServer }

    $result = Set-AuthenticodeSignature @signArgs
    # A signature is applied when a signer certificate is attached. Status 'Valid' means the
    # chain is also trusted on this machine; 'UnknownError' with a signer typically means the
    # signature is applied but the (self-signed / non-installed CA) chain is not trusted here,
    # which is a target-machine trust concern, not a signing failure. 'NotSigned' or a missing
    # signer is a real failure, and 'HashMismatch' must never occur right after signing.
    if ($null -eq $result.SignerCertificate -or $result.Status -eq 'NotSigned' -or $result.Status -eq 'HashMismatch') {
        throw "Signing failed for $rel : $($result.Status) - $($result.StatusMessage)"
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer) -and $null -eq $result.TimeStamperCertificate) {
        throw "Timestamping failed for $rel (no timestamp certificate applied). Not reporting Passed."
    }
    $trustNote = if ($result.Status -eq 'Valid') { '' } else { " (applied; chain not trusted on this machine: $($result.Status))" }
    Write-Host "Signed: $rel$trustNote"
}

# ---- Refresh checksums (files changed) and update metadata --------------------------
New-ReleaseChecksumManifest -PackageRoot $packageRoot | Out-Null

$existing = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$metadata = New-ReleaseMetadata -Product $existing.product -Version $existing.version -GitCommit $existing.gitCommit `
    -TargetRuntime $existing.targetRuntime -PackageRoot $packageRoot -SigningStatus 'Signed' `
    -BuildTimestampUtc ([datetime]::Parse($existing.buildTimestampUtc, $null, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)) `
    -CertificateSubject $certificate.Subject -CertificateThumbprint $certificate.Thumbprint -TimestampServer $TimestampServer
Write-ReleaseMetadata -Metadata $metadata -Path $metadataPath | Out-Null

$verify = Test-ReleaseChecksum -PackageRoot $packageRoot
if (-not $verify.Ok) { throw 'Post-signing checksum verification failed.' }

Write-Host ''
Write-Host "Signed $($targets.Count) file(s). signingStatus is now Signed. Checksums refreshed and verified."
