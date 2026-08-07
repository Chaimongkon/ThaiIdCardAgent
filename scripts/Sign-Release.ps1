#requires -Version 5.1
<#
.SYNOPSIS
    Applies Authenticode code signatures to a ThaiIdCardAgent release package and rebuilds the
    package artifacts so the shipped ZIP always contains the signed binaries.

.DESCRIPTION
    Signs exactly the files named by the signing allowlist (scripts\signing-allowlist.json), then
    verifies every signature before any checksum, manifest, or ZIP is produced. The stage order is:

        Sign -> Verify signatures and timestamps -> Checksums -> Manifest -> ZIP -> Verify ZIP

    Certificate sources: the Windows certificate store by thumbprint (the production path, which is
    also how a hardware token or HSM key is addressed through its CSP/KSP), or a PFX file whose
    password is supplied as a SecureString (development only).

    Backends:
      SignTool   - production. signtool.exe applies a SHA-256 Authenticode signature and an
                   RFC 3161 timestamp (/tr /td). Required for production because
                   Set-AuthenticodeSignature can only apply the legacy Authenticode timestamp.
      PowerShell - development/test only. Set-AuthenticodeSignature; -RequireRfc3161Timestamp
                   cannot be satisfied by this backend.

    Fails closed. The package is never marked Signed when any of these occur: no certificate,
    missing private key, wrong or missing Code Signing EKU, expired or not-yet-valid certificate,
    certificate too close to expiry, signer mismatch, timestamping failure, invalid signature,
    checksum/hash mismatch, an unsigned required file, or unexpected executable content in the
    payload.

.NOTES
    Windows PowerShell 5.1 compatible. Supports -WhatIf. A PIN, token password, or PFX password is
    never written to output, logs, the manifest, the package, or a command line.
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,

    [Parameter(ParameterSetName = 'Store')][string]$CertificateThumbprint,
    [Parameter(ParameterSetName = 'Store')][ValidateSet('CurrentUser', 'LocalMachine')][string]$StoreLocation = 'CurrentUser',

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)][string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')][System.Security.SecureString]$PfxPassword,

    [Parameter(ParameterSetName = 'Unsigned', Mandatory = $true)][switch]$Unsigned,

    # Production configuration (certificate identity, RFC 3161 timestamp URL, allowlist path).
    # Copy scripts\signing-config.template.json, fill in the confirmed values, keep it out of Git.
    [string]$SigningConfigPath,

    [string]$TimestampServer,
    [string]$ExpectedSignerSubject,
    [string]$ExpectedSignerIssuer,
    [string]$AllowlistPath,
    [string]$SignToolPath,
    [ValidateSet('SignTool', 'PowerShell')][string]$Backend,
    [ValidateSet('SHA256', 'SHA384', 'SHA512')][string]$HashAlgorithm = 'SHA256',
    [int]$MinimumCertificateRemainingDays = 0,

    # Production requires an RFC 3161 timestamp and a trusted chain. Both default on for the
    # SignTool backend; -RequireRfc3161Timestamp:$false exists only for development signing.
    [switch]$RequireRfc3161Timestamp,
    [switch]$RequireTrustedChain,

    # Rebuild the package ZIP after signing. On by default: a ZIP produced before signing would
    # ship unsigned binaries.
    [bool]$RebuildZip = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$payloadDir = Join-Path $packageRoot 'app'
$metadataPath = Join-Path $packageRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $payloadDir)) { throw "Package payload folder was not found: $payloadDir" }
if (-not (Test-Path -LiteralPath $metadataPath)) { throw "release-manifest.json was not found: $metadataPath" }

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

# ---- Resolve signing options (config + overrides, fail closed on placeholders) --------
# A -SigningConfigPath means "production run": RFC 3161 timestamping, a trusted chain, and the
# signtool backend are all required unless the operator explicitly overrides them. Without a
# config file this is an ad-hoc development signature and those requirements default off, but any
# explicitly passed switch always wins.
$usePfx = ($PSCmdlet.ParameterSetName -eq 'Pfx')
$hasConfig = -not [string]::IsNullOrWhiteSpace($SigningConfigPath)

$optionArgs = @{
    ConfigPath            = $SigningConfigPath
    ExpectedSignerSubject = $ExpectedSignerSubject
    ExpectedSignerIssuer  = $ExpectedSignerIssuer
    TimestampServerUrl    = $TimestampServer
    SignToolPath          = $SignToolPath
    AllowlistPath         = $AllowlistPath
}
if ($usePfx) {
    # A PFX carries its own identity; the thumbprint requirement is satisfied once it is loaded.
    $optionArgs['CertificateThumbprint'] = 'pfx'
}
else {
    $optionArgs['CertificateThumbprint'] = $CertificateThumbprint
    $optionArgs['StoreLocation'] = $StoreLocation
}
if ($PSBoundParameters.ContainsKey('RequireRfc3161Timestamp')) { $optionArgs['RequireRfc3161Timestamp'] = [bool]$RequireRfc3161Timestamp }
if ($PSBoundParameters.ContainsKey('RequireTrustedChain')) { $optionArgs['RequireTrustedChain'] = [bool]$RequireTrustedChain }
if (-not $hasConfig -and [string]::IsNullOrWhiteSpace($TimestampServer)) {
    # Sentinel: no timestamp service was configured at all.
    $optionArgs['TimestampServerUrl'] = 'none'
}
$options = New-ReleaseSigningOption @optionArgs

$timestampUrl = $options.TimestampServerUrl
$timestampConfigured = ($timestampUrl -ne 'none' -and -not [string]::IsNullOrWhiteSpace($timestampUrl))

$requireRfc3161 =
if ($PSBoundParameters.ContainsKey('RequireRfc3161Timestamp')) { [bool]$RequireRfc3161Timestamp }
elseif ($hasConfig) { [bool]$options.RequireRfc3161Timestamp }
else { $false }

$requireTrustedChain =
if ($PSBoundParameters.ContainsKey('RequireTrustedChain')) { [bool]$RequireTrustedChain }
elseif ($hasConfig) { [bool]$options.RequireTrustedChain }
else { $false }

$effectiveBackend =
if (-not [string]::IsNullOrWhiteSpace($Backend)) { $Backend }
elseif ($usePfx) { 'PowerShell' }
elseif ($hasConfig) { $options.Backend }
else { 'PowerShell' }

$digestAlgorithm = if ($PSBoundParameters.ContainsKey('HashAlgorithm')) { $HashAlgorithm } else { $options.FileDigestAlgorithm }
$minimumRemainingDays = if ($PSBoundParameters.ContainsKey('MinimumCertificateRemainingDays')) { $MinimumCertificateRemainingDays } else { $options.MinimumRemainingDays }

if (-not $timestampConfigured) {
    if ($requireRfc3161) {
        throw 'No timestamp server is configured but an RFC 3161 timestamp is required. Supply -TimestampServer / timestampServerUrl, or pass -RequireRfc3161Timestamp:$false for a development signature.'
    }
    Write-Warning 'No timestamp server supplied: signatures will become invalid when the certificate expires. Production releases must be timestamped.'
}

# ---- Signing allowlist and plan (fail closed before touching any file) ---------------
$policy = Get-ReleaseSigningPolicy -PolicyPath $options.AllowlistPath
$plan = Resolve-ReleaseSigningPlan -PackageRoot $packageRoot -Policy $policy
if (-not $plan.Ok) {
    throw "Refusing to sign. Signing allowlist violations:`n  " + ($plan.Messages -join "`n  ")
}
if ($plan.SignTargets.Count -eq 0) {
    throw 'No signable files were found in the package payload.'
}
Write-Host "Signing allowlist : $($policy.PolicyPath)"
Write-Host "Required files    : $($plan.Required.Count)"
Write-Host "Files to sign     : $($plan.SignTargets.Count)"

# ---- Resolve and validate the signing certificate ------------------------------------
$certificate = $null
$effectiveStore = if ($PSBoundParameters.ContainsKey('StoreLocation')) { $StoreLocation } else { $options.StoreLocation }
if ($usePfx) {
    if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX file was not found: $PfxPath" }
    # Never Exportable: the private key must not be extractable from this process.
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    if ($null -eq $PfxPassword) { $PfxPassword = [System.Security.SecureString]::new() }
    # The SecureString is passed straight to the constructor; it is never converted to text.
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $PfxPassword, $flags)
}
else {
    $thumbprintSource = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $options.CertificateThumbprint } else { $CertificateThumbprint }
    if ([string]::IsNullOrWhiteSpace($thumbprintSource) -or $thumbprintSource -eq 'pfx') {
        throw 'Specify -CertificateThumbprint (store/token/HSM), -PfxPath (file), or -Unsigned (pilot).'
    }
    $normalized = ($thumbprintSource -replace '\s', '').ToUpperInvariant()
    $storePath = "Cert:\$effectiveStore\My\$normalized"
    if (-not (Test-Path -LiteralPath $storePath)) {
        throw "Code signing certificate not found in $effectiveStore\My for the supplied thumbprint. For a hardware token or HSM, confirm the token is inserted and its CSP/KSP has registered the certificate."
    }
    $certificate = Get-Item -LiteralPath $storePath
}

Test-CodeSigningCertificate -Certificate $certificate `
    -ExpectedSubject $options.ExpectedSignerSubject -ExpectedIssuer $options.ExpectedSignerIssuer `
    -MinimumRemainingDays $minimumRemainingDays | Out-Null

Write-Host "Signing certificate subject   : $($certificate.Subject)"
Write-Host "Signing certificate issuer    : $($certificate.Issuer)"
Write-Host "Signing certificate thumbprint: $($certificate.Thumbprint)"
Write-Host "Certificate valid until       : $($certificate.NotAfter.ToUniversalTime().ToString('o'))"
Write-Host "Backend                       : $effectiveBackend"
$timestampDisplay = if ($timestampConfigured) { $timestampUrl } else { '(none)' }
Write-Host "Timestamp service             : $timestampDisplay"

if ($effectiveBackend -eq 'PowerShell' -and $requireRfc3161) {
    throw 'The PowerShell backend applies the legacy Authenticode timestamp, which cannot satisfy -RequireRfc3161Timestamp. Use the SignTool backend for production.'
}
if ($usePfx -and $effectiveBackend -eq 'SignTool') {
    # signtool would need the PFX password as a /p command-line argument, which is exactly what the
    # credential-handling rules forbid. PFX signing stays on the in-process PowerShell backend.
    throw 'PFX signing through signtool would require the password on the command line. Use -CertificateThumbprint with the certificate store (the production path for a token or HSM key), or -Backend PowerShell for development signing.'
}

$signToolExe = $null
if ($effectiveBackend -eq 'SignTool') {
    $signToolExe = Get-SignToolPath -SignToolPath $options.SignToolPath
    if ($null -eq $signToolExe) {
        throw 'signtool.exe was not found. Install the Windows SDK signing tools, or set signToolPath in the signing configuration.'
    }
    Write-Host "signtool                      : $signToolExe"
}

if (-not $PSCmdlet.ShouldProcess($packageRoot, "Sign $($plan.SignTargets.Count) file(s), refresh checksums and manifest, rebuild ZIP")) {
    Write-Host 'WhatIf: no files were signed and no package artifact was modified.'
    foreach ($target in $plan.SignTargets) { Write-Host "WhatIf would sign: $($target.RelativePath)" }
    return
}

# ---- Stage 1: sign -------------------------------------------------------------------
$targetPaths = @($plan.SignTargets | ForEach-Object { $_.FullName })

if ($effectiveBackend -eq 'SignTool') {
    $signArgs = @('sign', '/fd', $digestAlgorithm, '/sha1', ($certificate.Thumbprint -replace '\s', ''))
    if ($effectiveStore -eq 'LocalMachine') { $signArgs += '/sm' }
    if ($timestampConfigured) { $signArgs += @('/tr', $timestampUrl, '/td', $options.TimestampDigestAlgorithm) }
    if ($options.AdditionalSignToolArgs.Count -gt 0) {
        Test-SignToolArgumentSafety -Argument $options.AdditionalSignToolArgs | Out-Null
        $signArgs += $options.AdditionalSignToolArgs
    }
    $signArgs += $targetPaths

    # signtool prompts for the token PIN itself when the CSP/KSP requires one. The PIN is never
    # passed on the command line, so it cannot leak into logs, evidence, or process listings.
    & $signToolExe @signArgs | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed with exit code $LASTEXITCODE. No package artifact was updated; the package remains unsigned."
    }
}
else {
    foreach ($target in $plan.SignTargets) {
        $setArgs = @{
            FilePath      = $target.FullName
            Certificate   = $certificate
            HashAlgorithm = $digestAlgorithm
        }
        if ($timestampConfigured) { $setArgs['TimestampServer'] = $timestampUrl }
        $result = Set-AuthenticodeSignature @setArgs
        if ($null -eq $result.SignerCertificate -or $result.Status -eq 'NotSigned' -or $result.Status -eq 'HashMismatch') {
            throw "Signing failed for $($target.RelativePath): $($result.Status) - $($result.StatusMessage)"
        }
        if ($timestampConfigured -and $null -eq $result.TimeStamperCertificate) {
            throw "Timestamping failed for $($target.RelativePath) (no timestamp certificate applied). Not reporting Passed."
        }
        Write-Host "Signed: $($target.RelativePath)"
    }
}

# ---- Stage 2: verify signatures and timestamps BEFORE producing any artifact ---------
$reportArgs = @{
    PackageRoot             = $packageRoot
    Policy                  = $policy
    ExpectedThumbprint      = $certificate.Thumbprint
    ExpectedSubject         = $certificate.Subject
    RequiredDigestAlgorithm = $digestAlgorithm
    TimestampServerUrl      = if ($timestampConfigured) { $timestampUrl } else { $null }
    RequireTimestamp        = $timestampConfigured
    RequireRfc3161Timestamp = $requireRfc3161
    RequireTrustedChain     = $requireTrustedChain
}
$report = New-ReleaseSigningReport @reportArgs
foreach ($file in $report.Files) {
    $note = if ($file.Timestamped) { " ($($file.TimestampKind) timestamp)" } else { ' (no timestamp)' }
    $verdict = if ($file.Ok) { 'Verified' } else { 'FAILED' }
    Write-Host ("{0,-9}: {1} [{2}]{3}" -f $verdict, $file.RelativePath, $file.DigestAlgorithm, $note)
}
if (-not $report.Ok) {
    throw "Signature verification FAILED after signing. The package is NOT marked Signed.`n  " + ($report.Messages -join "`n  ")
}
Write-Host "Signature verification: PASSED ($($report.SignedFileCount)/$($report.SignTargetCount) file(s))."

# ---- Stage 3-4: checksums, then manifest with the signing evidence -------------------
New-ReleaseChecksumManifest -PackageRoot $packageRoot | Out-Null

$existing = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$metadata = New-ReleaseMetadata -Product $existing.product -Version $existing.version -GitCommit $existing.gitCommit `
    -TargetRuntime $existing.targetRuntime -PackageRoot $packageRoot -SigningStatus 'Signed' `
    -BuildTimestampUtc ([datetime]::Parse($existing.buildTimestampUtc, $null, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)) `
    -SigningReport $report
Write-ReleaseMetadata -Metadata $metadata -Path $metadataPath | Out-Null

$verify = Test-ReleaseChecksum -PackageRoot $packageRoot
if (-not $verify.Ok) { throw 'Post-signing checksum verification failed.' }

# ---- Stage 5-6: rebuild the ZIP from the signed package, then verify the ZIP ---------
if ($RebuildZip) {
    $zipPath = Join-Path ([System.IO.Path]::GetDirectoryName($packageRoot)) ((Split-Path -Leaf $packageRoot) + '.zip')
    New-ReleasePackageZip -PackageRoot $packageRoot -DestinationZip $zipPath | Out-Null
    Write-Host "Package zip rebuilt from the signed payload: $zipPath"

    $zipCheckArgs = @{
        Policy                  = $policy
        ExpectedThumbprint      = $certificate.Thumbprint
        RequiredDigestAlgorithm = $digestAlgorithm
        RequireTimestamp        = $timestampConfigured
        RequireRfc3161Timestamp = $requireRfc3161
        RequireTrustedChain     = $requireTrustedChain
    }
    $zipResult = Test-ReleaseZipIntegrity -ZipPath $zipPath -RequireSigned -SigningReportParameters $zipCheckArgs
    if (-not $zipResult.Ok) {
        throw "Final package (ZIP) verification FAILED.`n  " + ($zipResult.Messages -join "`n  ")
    }
    Write-Host 'Final package (ZIP) verification: PASSED.'
}
else {
    Write-Warning 'ZIP rebuild was skipped (-RebuildZip:$false). Any existing ZIP still contains the pre-signing payload and must not be distributed.'
}

Write-Host ''
Write-Host "Signed $($report.SignTargetCount) file(s). signingStatus is now Signed."
Write-Host "Signature algorithm: $($report.SignatureAlgorithm)  Timestamp: $($report.TimestampKind)"
