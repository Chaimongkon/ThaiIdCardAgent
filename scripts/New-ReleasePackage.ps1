#requires -Version 5.1
<#
.SYNOPSIS
    Builds a reproducible, verifiable ThaiIdCardAgent release package.

.DESCRIPTION
    Publishes the Windows service (win-x64, self-contained single file), assembles a
    versioned package folder, refuses to include secrets, writes a SHA-256 checksum
    manifest plus a release-manifest.json, and zips the result.

    The package is produced as UnsignedPilot. Run scripts\Sign-Release.ps1 afterwards to
    apply Authenticode signatures and flip the package to Signed.

.NOTES
    Windows PowerShell 5.1 compatible. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0-pilot',
    [string]$OutputRoot,
    [string]$PublishPath,
    # Excluded: debug symbols, Development settings, and IIS/static-asset publish artifacts that
    # the Kestrel Windows Service never uses (web.config + ANCM native module + static web assets
    # manifest; the app self-hosts via UseWindowsService and serves no static files).
    [string[]]$ExcludeFromPayload = @(
        '*.pdb',
        'appsettings.Development.json', 'appsettings.*.Development.json',
        'web.config',
        'aspnetcorev2_inprocess.dll',
        '*.staticwebassets.endpoints.json'
    ),
    [switch]$SkipPublish,

    # Production flow: stop after the payload is assembled and validated so scripts\Sign-Release.ps1
    # can sign first and then produce checksums, manifest and ZIP from the SIGNED payload. Without
    # this an unsigned ZIP is written and later overwritten, which must never be distributed.
    [switch]$SkipZip,

    # Path to the signing allowlist used to reject unexpected executable content in the payload.
    [string]$AllowlistPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePackaging.psm1') -Force -DisableNameChecking

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$product = 'ThaiIdCardAgent'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root 'artifacts\release' }
$resolvedOutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)

if ([string]::IsNullOrWhiteSpace($PublishPath)) { $PublishPath = Join-Path $root 'artifacts\publish\win-x64' }
$resolvedPublishPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishPath)

$packageName = "$product-$Version-$Runtime"
$packageRoot = Join-Path $resolvedOutputRoot $packageName
$payloadDir = Join-Path $packageRoot 'app'
$zipPath = Join-Path $resolvedOutputRoot "$packageName.zip"

function Get-GitCommit {
    try {
        $commit = (& git -C $root rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($commit)) {
            $status = (& git -C $root status --porcelain 2>$null)
            $suffix = if ([string]::IsNullOrWhiteSpace($status)) { '' } else { '-dirty' }
            return ($commit.Trim() + $suffix)
        }
    }
    catch { }
    return 'unknown'
}

$gitCommit = Get-GitCommit
$buildTime = [datetime]::UtcNow

Write-Host "Product        : $product"
Write-Host "Version        : $Version"
Write-Host "Runtime        : $Runtime"
Write-Host "Git commit     : $gitCommit"
Write-Host "Package folder : $packageRoot"
Write-Host "Zip            : $zipPath"

if (-not $PSCmdlet.ShouldProcess($packageRoot, "Build $product $Version release package")) {
    Write-Host 'WhatIf: no publish, copy, manifest, or zip was performed.'
    return
}

# 1. Publish (unless caller supplied an existing publish output).
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'Publish-WinX64.ps1') -Configuration $Configuration -Runtime $Runtime -OutputPath $resolvedPublishPath
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
}

$sourceExe = Join-Path $resolvedPublishPath 'ThaiIdCardAgent.Service.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Publish output was not found. Expected: $sourceExe"
}

# 2. Assemble a clean package folder.
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
Get-ChildItem -LiteralPath $resolvedPublishPath -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $payloadDir -Recurse -Force
}

# 2b. Drop non-runtime files (debug symbols, Development settings) so only necessary files ship.
if ($ExcludeFromPayload -and $ExcludeFromPayload.Count -gt 0) {
    Get-ChildItem -LiteralPath $payloadDir -Recurse -File -Force | ForEach-Object {
        $file = $_
        foreach ($pattern in $ExcludeFromPayload) {
            if ($file.Name -like $pattern) {
                Remove-Item -LiteralPath $file.FullName -Force
                Write-Host "Excluded from payload: $($file.Name)"
                break
            }
        }
    }
}

# 3. Refuse to ship secrets.
$secrets = @(Test-ReleaseSecretExclusion -Path $payloadDir)
if ($secrets.Count -gt 0) {
    throw "Refusing to package. Forbidden secret files detected: $($secrets -join ', ')"
}

# 3b. Refuse to ship executable content the signing allowlist does not account for, and refuse a
# payload that is missing a file the allowlist marks as required-signed. This runs for unsigned
# pilot builds too: an unexpected binary in the payload is a packaging fault either way.
$signingPolicy = if ([string]::IsNullOrWhiteSpace($AllowlistPath)) { Get-ReleaseSigningPolicy } else { Get-ReleaseSigningPolicy -PolicyPath $AllowlistPath }
$signingPlan = Resolve-ReleaseSigningPlan -PackageRoot $packageRoot -Policy $signingPolicy
if (-not $signingPlan.Ok) {
    throw "Refusing to package. Signing allowlist violations:`n  " + ($signingPlan.Messages -join "`n  ")
}
Write-Host "Signing allowlist: $($signingPolicy.PolicyPath) ($($signingPlan.Required.Count) required, $($signingPlan.SignTargets.Count) signable)"

# 4. SHA-256 checksum manifest (deterministic ordering).
$manifestPath = New-ReleaseChecksumManifest -PackageRoot $packageRoot
Write-Host "Checksum manifest: $manifestPath"

# 5. release-manifest.json metadata (UnsignedPilot).
$metadata = New-ReleaseMetadata -Product $product -Version $Version -GitCommit $gitCommit `
    -TargetRuntime $Runtime -PackageRoot $packageRoot -SigningStatus 'UnsignedPilot' -BuildTimestampUtc $buildTime
$metadataPath = Join-Path $packageRoot 'release-manifest.json'
Write-ReleaseMetadata -Metadata $metadata -Path $metadataPath | Out-Null
Write-Host "Release metadata : $metadataPath"

# 6. Deterministic zip of the whole package folder.
if (-not $SkipZip) {
    New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
    New-ReleasePackageZip -PackageRoot $packageRoot -DestinationZip $zipPath | Out-Null
    Write-Host "Package zip      : $zipPath"
}
else {
    # Production ordering: nothing is zipped until the payload has been signed and verified.
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Write-Host 'Package zip      : skipped (-SkipZip). Sign-Release.ps1 produces the ZIP from the signed payload.'
}

# 7. Final verification pass so New-ReleasePackage never emits a broken package.
$verify = Test-ReleaseChecksum -PackageRoot $packageRoot
if (-not $verify.Ok) {
    throw "Post-build checksum verification failed. Missing=$($verify.Missing -join ',') Modified=$($verify.Modified -join ',') Extra=$($verify.Extra -join ',')"
}
if (-not $SkipZip) {
    $zipVerify = Test-ReleaseZipIntegrity -ZipPath $zipPath
    if (-not $zipVerify.Ok) {
        throw "Final package (ZIP) verification failed.`n  " + ($zipVerify.Messages -join "`n  ")
    }
    Write-Host 'Final package (ZIP) verification: PASSED.'
}

Write-Host ''
Write-Host "Release package created (UnsignedPilot). File count: $($metadata.fileCount)."
Write-Host "Next: sign with scripts\Sign-Release.ps1 or ship as an explicitly unsigned pilot build."
