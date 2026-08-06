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
    [switch]$SkipPublish
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

function New-DeterministicZip {
    param([string]$SourceDir, [string]$DestinationZip)

    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    if (Test-Path -LiteralPath $DestinationZip) { Remove-Item -LiteralPath $DestinationZip -Force }

    $sourceFull = (Resolve-Path -LiteralPath $SourceDir).Path
    $files = Get-ChildItem -LiteralPath $sourceFull -Recurse -File -Force
    $relatives = @()
    foreach ($f in $files) {
        $relatives += (Get-ReleaseRelativePath -Root $sourceFull -FullName $f.FullName)
    }
    $relatives = @(Get-OrdinalSortedString -Value $relatives)

    # Fixed timestamp for reproducibility (entries otherwise carry file mtimes).
    $fixedTime = [System.DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    $stream = [System.IO.File]::Open($DestinationZip, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($rel in $relatives) {
                $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTime
                $sourceFile = Join-Path $sourceFull ($rel -replace '/', '\')
                $entryStream = $entry.Open()
                try {
                    $bytes = [System.IO.File]::ReadAllBytes($sourceFile)
                    $entryStream.Write($bytes, 0, $bytes.Length)
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
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
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
New-DeterministicZip -SourceDir $packageRoot -DestinationZip $zipPath
Write-Host "Package zip      : $zipPath"

# 7. Final verification pass so New-ReleasePackage never emits a broken package.
$verify = Test-ReleaseChecksum -PackageRoot $packageRoot
if (-not $verify.Ok) {
    throw "Post-build checksum verification failed. Missing=$($verify.Missing -join ',') Modified=$($verify.Modified -join ',') Extra=$($verify.Extra -join ',')"
}

Write-Host ''
Write-Host "Release package created (UnsignedPilot). File count: $($metadata.fileCount)."
Write-Host "Next: sign with scripts\Sign-Release.ps1 or ship as an explicitly unsigned pilot build."
