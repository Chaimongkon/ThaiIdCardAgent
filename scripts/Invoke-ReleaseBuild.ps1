#requires -Version 5.1
<#
.SYNOPSIS
    Runs the complete ThaiIdCardAgent release build in the mandatory order.

.DESCRIPTION
    This is the single entry point for a production release. It enforces the stage order that the
    individual scripts cannot enforce on their own:

        1. Publish                     (New-ReleasePackage.ps1)
        2. Assemble + validate payload (secret exclusion, signing allowlist)
        3. Sign binaries/scripts       (Sign-Release.ps1)
        4. Verify signatures + timestamps
        5. Generate checksums
        6. Generate release-manifest.json (with signing evidence)
        7. Create ZIP
        8. Verify the final package (extracted from the ZIP)

    Stages 4-8 all run inside Sign-Release.ps1 so no artifact is ever produced from an unverified
    payload, and no ZIP is ever produced from an unsigned payload: stage 1-2 runs with -SkipZip.

    -Unsigned builds an explicitly UnsignedPilot package instead (stages 1, 2, 5, 6, 7, 8). It is
    for controlled pilot machines only and never produces a Signed package.

    Fails closed at every stage. The release is Signed only when every required signature verified.

.NOTES
    Windows PowerShell 5.1 compatible. Supports -WhatIf. Never accepts or echoes a PIN or password.
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Signed')]
param(
    [Parameter(Mandatory = $true)][string]$Version,

    [Parameter(ParameterSetName = 'Signed', Mandatory = $true)][string]$SigningConfigPath,
    [Parameter(ParameterSetName = 'Unsigned', Mandatory = $true)][switch]$Unsigned,

    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot,
    [string]$PublishPath,
    [string]$AllowlistPath,
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root 'artifacts\release' }
$resolvedOutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
$packageRoot = Join-Path $resolvedOutputRoot "ThaiIdCardAgent-$Version-$Runtime"

$signing = -not $Unsigned

# Validate the signing configuration BEFORE publishing anything: an unresolved <PLACEHOLDER>, a
# secret-bearing key, or a missing certificate must stop the run before it produces artifacts.
if ($signing) {
    $options = New-ReleaseSigningOption -ConfigPath $SigningConfigPath -AllowlistPath $AllowlistPath
    Write-Host 'Signing configuration validated.'
    Write-Host "  Backend            : $($options.Backend)"
    Write-Host "  Certificate source : $($options.CertificateSource) ($($options.StoreLocation))"
    Write-Host "  Thumbprint         : $($options.CertificateThumbprint)"
    Write-Host "  Timestamp service  : $($options.TimestampServerUrl)"
    Write-Host "  Allowlist          : $($options.AllowlistPath)"
}
else {
    Write-Warning 'UNSIGNED PILOT BUILD: the resulting package is not Authenticode signed and must not be distributed publicly.'
}

$buildDescription = if ($signing) { 'Build and sign release' } else { 'Build unsigned pilot release' }
if (-not $PSCmdlet.ShouldProcess($packageRoot, "$buildDescription $Version")) {
    Write-Host 'WhatIf: no publish, signing, manifest, or ZIP was produced.'
    return
}

# ---- Stages 1-2: publish and assemble a validated payload ----------------------------
Write-Host ''
Write-Host '=== Stage 1-2: publish and assemble payload ==='
$packageArgs = @{
    Configuration = $Configuration
    Runtime       = $Runtime
    Version       = $Version
    OutputRoot    = $resolvedOutputRoot
}
if (-not [string]::IsNullOrWhiteSpace($PublishPath)) { $packageArgs['PublishPath'] = $PublishPath }
if (-not [string]::IsNullOrWhiteSpace($AllowlistPath)) { $packageArgs['AllowlistPath'] = $AllowlistPath }
if ($SkipPublish) { $packageArgs['SkipPublish'] = $true }
# The ZIP is deliberately deferred: a signed release must never have an unsigned ZIP on disk.
if ($signing) { $packageArgs['SkipZip'] = $true }

& (Join-Path $PSScriptRoot 'New-ReleasePackage.ps1') @packageArgs
if (-not (Test-Path -LiteralPath $packageRoot)) { throw "Package folder was not produced: $packageRoot" }

if (-not $signing) {
    Write-Host ''
    Write-Host '=== Unsigned pilot: recording UnsignedPilot status ==='
    & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -PackagePath $packageRoot -Unsigned
    Write-Host ''
    Write-Host "UnsignedPilot release built: $packageRoot"
    return
}

# ---- Stages 3-8: sign, verify, checksums, manifest, ZIP, verify ZIP ------------------
Write-Host ''
Write-Host '=== Stage 3-8: sign, verify, checksum, manifest, zip, verify package ==='
$signArgs = @{
    PackagePath       = $packageRoot
    SigningConfigPath = $SigningConfigPath
}
if (-not [string]::IsNullOrWhiteSpace($AllowlistPath)) { $signArgs['AllowlistPath'] = $AllowlistPath }
& (Join-Path $PSScriptRoot 'Sign-Release.ps1') @signArgs

# ---- Independent confirmation with the standalone verifier --------------------------
Write-Host ''
Write-Host '=== Independent verification ==='
& (Join-Path $PSScriptRoot 'Test-ReleaseSignature.ps1') -PackagePath $packageRoot `
    -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain `
    -ExpectedSignerThumbprint $options.CertificateThumbprint

Write-Host ''
Write-Host "Signed release built and verified: $packageRoot"
Write-Host "ZIP: $packageRoot.zip"
