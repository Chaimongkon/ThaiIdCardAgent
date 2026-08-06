#requires -Version 5.1
Set-StrictMode -Version Latest

# Shared, unit-testable helpers for building and verifying ThaiIdCardAgent release
# packages. The scripts (New-ReleasePackage / Sign-Release / Test-ReleaseSignature /
# Install-Service) import this module so the security-relevant logic lives in one place
# and is covered by the ThaiIdCardAgent.Release.Tests project.

# Filename-based patterns that must never appear inside a release package.
$script:ReleaseSecretPatterns = @(
    '*.pfx', '*.p12', '*.pvk', '*.snk',
    '*.key', '*.pem',
    '*.jwt',
    '.env', '.env.local', '.env.*.local', '*.env.local',
    'appsettings.*.local.json',
    'id_rsa', 'id_rsa.*',
    '*.log'
)

# Code Signing EKU (never Server Authentication 1.3.6.1.5.5.7.3.1).
$script:CodeSigningEku = '1.3.6.1.5.5.7.3.3'

function Get-ReleaseSecretPattern {
    # Exposed for tests / diagnostics.
    return , $script:ReleaseSecretPatterns
}

function Get-Sha256Hex {
    # SHA-256 as uppercase hex, computed with .NET directly. Get-FileHash returns nothing under
    # -WhatIf (its provider path resolution is WhatIf-suppressed), so it must not be used here.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($LiteralPath)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Get-OrdinalSortedString {
    [CmdletBinding()]
    param([Parameter(Mandatory = $false)][AllowNull()][AllowEmptyCollection()][string[]]$Value)

    $list = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $Value) {
        foreach ($item in $Value) { [void]$list.Add($item) }
    }
    $list.Sort([System.StringComparer]::Ordinal)
    # Emit unrolled so callers can wrap with @() to get a clean 0/1/many array.
    return $list.ToArray()
}

function Get-ReleaseRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$FullName
    )

    $normalizedRoot = $Root.TrimEnd('\', '/')
    $relative = $FullName.Substring($normalizedRoot.Length).TrimStart('\', '/')
    return ($relative -replace '\\', '/')
}

function Get-ReleaseFileInventory {
    <#
        Returns a deterministically ordered inventory of every file under $Path.
        Ordering is ordinal on the forward-slash relative path so it does not vary by locale.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $root = (Resolve-Path -LiteralPath $Path).Path
    $records = @{}
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $_.FullName
        $records[$rel] = [pscustomobject]@{
            Path   = $rel
            Length = $_.Length
        }
    }

    $ordered = @()
    foreach ($key in (Get-OrdinalSortedString -Value @($records.Keys))) {
        $ordered += $records[$key]
    }
    return , $ordered
}

function Test-ReleaseSecretExclusion {
    <#
        Scans $Path for any file whose name matches a forbidden secret pattern.
        Returns the list of offending relative paths (empty = clean).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $root = (Resolve-Path -LiteralPath $Path).Path
    $violations = @()
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $name = $_.Name
        foreach ($pattern in $script:ReleaseSecretPatterns) {
            if ($name -like $pattern) {
                $violations += (Get-ReleaseRelativePath -Root $root -FullName $_.FullName)
                break
            }
        }
    }
    # Emit unrolled so callers wrap with @() for a clean 0/1/many array.
    return (Get-OrdinalSortedString -Value @($violations))
}

function Get-ReleasePayloadFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [string]$PayloadSubdirectory = 'app'
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path
    $payload = Join-Path $root $PayloadSubdirectory
    if (-not (Test-Path -LiteralPath $payload)) {
        throw "Package payload directory was not found: $payload"
    }
    return Get-ChildItem -LiteralPath $payload -Recurse -File -Force
}

function New-ReleaseChecksumManifest {
    <#
        Computes SHA-256 for every payload file and writes a deterministic manifest
        (UTF-8 without BOM, LF line endings, ordinal sort). Line format:
            <64-hex-uppercase><two spaces><forward-slash relative path>
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [string]$PayloadSubdirectory = 'app',
        [string]$ManifestName = 'checksums.sha256'
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path
    $files = Get-ReleasePayloadFile -PackageRoot $root -PayloadSubdirectory $PayloadSubdirectory

    $hashes = @{}
    foreach ($file in $files) {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $file.FullName
        $hashes[$rel] = Get-Sha256Hex -LiteralPath $file.FullName
    }

    $builder = [System.Text.StringBuilder]::new()
    foreach ($key in (Get-OrdinalSortedString -Value @($hashes.Keys))) {
        [void]$builder.Append($hashes[$key])
        [void]$builder.Append('  ')
        [void]$builder.Append($key)
        [void]$builder.Append("`n")
    }

    $manifestPath = Join-Path $root $ManifestName
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($manifestPath, $builder.ToString(), $utf8NoBom)
    return $manifestPath
}

function Read-ReleaseChecksumManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Checksum manifest was not found: $ManifestPath"
    }

    $expected = @{}
    $lines = [System.IO.File]::ReadAllLines($ManifestPath)
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64})\s{1,}(.+?)\s*$')
        if (-not $match.Success) {
            throw "Malformed checksum manifest line: $line"
        }
        $relative = $match.Groups[2].Value.Trim()
        $expected[$relative] = $match.Groups[1].Value.ToUpperInvariant()
    }

    if ($expected.Count -eq 0) {
        throw "Checksum manifest contained no entries: $ManifestPath"
    }
    return $expected
}

function Test-ReleaseChecksum {
    <#
        Verifies payload files against the checksum manifest.
        Returns an object with Ok plus Missing/Modified/Extra relative-path lists.
        Throws when the manifest is missing or malformed (fail closed).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [string]$PayloadSubdirectory = 'app',
        [string]$ManifestName = 'checksums.sha256'
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path
    $manifestPath = Join-Path $root $ManifestName
    $expected = Read-ReleaseChecksumManifest -ManifestPath $manifestPath

    $actual = @{}
    foreach ($file in (Get-ReleasePayloadFile -PackageRoot $root -PayloadSubdirectory $PayloadSubdirectory)) {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $file.FullName
        $actual[$rel] = Get-Sha256Hex -LiteralPath $file.FullName
    }

    $missing = @()
    $modified = @()
    foreach ($rel in $expected.Keys) {
        if (-not $actual.ContainsKey($rel)) {
            $missing += $rel
        }
        elseif ($actual[$rel] -ne $expected[$rel]) {
            $modified += $rel
        }
    }

    $extra = @()
    foreach ($rel in $actual.Keys) {
        if (-not $expected.ContainsKey($rel)) {
            $extra += $rel
        }
    }

    $missing = @(Get-OrdinalSortedString -Value @($missing))
    $modified = @(Get-OrdinalSortedString -Value @($modified))
    $extra = @(Get-OrdinalSortedString -Value @($extra))

    return [pscustomobject]@{
        Ok       = ($missing.Count -eq 0 -and $modified.Count -eq 0 -and $extra.Count -eq 0)
        Missing  = $missing
        Modified = $modified
        Extra    = $extra
    }
}

function Test-CodeSigningCertificate {
    <#
        Validates that a certificate is usable for Authenticode code signing.
        Throws (fail closed) for: no private key, missing Code Signing EKU,
        not-yet-valid, or expired. Rejects HTTPS/Server-Authentication certificates
        because they lack the Code Signing EKU.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [datetime]$Now = (Get-Date)
    )

    if (-not $Certificate.HasPrivateKey) {
        throw 'Signing certificate does not have an associated private key.'
    }

    $ekuExtension = $Certificate.Extensions |
        Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        Select-Object -First 1

    $hasCodeSigning = $false
    if ($ekuExtension) {
        $hasCodeSigning = [bool]($ekuExtension.EnhancedKeyUsages | Where-Object { $_.Value -eq $script:CodeSigningEku })
    }
    if (-not $hasCodeSigning) {
        throw "Certificate does not have the Code Signing EKU ($script:CodeSigningEku). A localhost/HTTPS certificate cannot be used for code signing."
    }

    if ($Now -lt $Certificate.NotBefore) {
        throw "Certificate is not yet valid (NotBefore=$($Certificate.NotBefore.ToUniversalTime().ToString('o')))."
    }
    if ($Now -gt $Certificate.NotAfter) {
        throw "Certificate has expired (NotAfter=$($Certificate.NotAfter.ToUniversalTime().ToString('o')))."
    }

    return $true
}

function New-ReleaseMetadata {
    <#
        Builds the release-manifest.json object. Contains no secrets. Certificate
        subject/thumbprint are only populated for signed packages.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Product,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$GitCommit,
        [Parameter(Mandatory = $true)][string]$TargetRuntime,
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [ValidateSet('UnsignedPilot', 'Signed')][string]$SigningStatus = 'UnsignedPilot',
        [string]$PayloadSubdirectory = 'app',
        [datetime]$BuildTimestampUtc = ([datetime]::UtcNow),
        [string]$CertificateSubject,
        [string]$CertificateThumbprint,
        [string]$TimestampServer
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path
    $files = @()
    foreach ($file in (Get-ReleasePayloadFile -PackageRoot $root -PayloadSubdirectory $PayloadSubdirectory)) {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $file.FullName
        $files += [pscustomobject]@{
            path   = $rel
            sha256 = Get-Sha256Hex -LiteralPath $file.FullName
            length = $file.Length
        }
    }

    # Deterministic ordering by relative path.
    $sortedFiles = @()
    $byPath = @{}
    foreach ($entry in $files) { $byPath[$entry.path] = $entry }
    foreach ($key in (Get-OrdinalSortedString -Value @($byPath.Keys))) { $sortedFiles += $byPath[$key] }

    $metadata = [ordered]@{
        product           = $Product
        version           = $Version
        gitCommit         = $GitCommit
        buildTimestampUtc = $BuildTimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        targetRuntime     = $TargetRuntime
        signingStatus     = $SigningStatus
        fileCount         = $sortedFiles.Count
        files             = $sortedFiles
    }

    if ($SigningStatus -eq 'Signed') {
        $metadata['signing'] = [ordered]@{
            certificateSubject    = $CertificateSubject
            certificateThumbprint = $CertificateThumbprint
            timestampServer       = $TimestampServer
        }
    }

    return $metadata
}

function Test-ReleaseZipEntry {
    <#
        Validates every entry name in a ZIP before extraction. Throws (fail closed) on an
        absolute path, a parent-directory traversal segment (..), or a duplicate file entry.
        Returns $true when the archive is safe.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrEmpty($name)) { continue }
            $normalized = $name -replace '\\', '/'

            if ([System.IO.Path]::IsPathRooted($normalized) -or $normalized -match '^[A-Za-z]:' -or $normalized.StartsWith('/')) {
                throw "Unsafe ZIP entry (absolute path): $name"
            }
            if (($normalized -split '/') -contains '..') {
                throw "Unsafe ZIP entry (path traversal): $name"
            }
            if (-not $normalized.EndsWith('/')) {
                if (-not $seen.Add($normalized)) {
                    throw "Duplicate ZIP entry: $name"
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    return $true
}

function Expand-ReleasePackage {
    <#
        Extracts a release ZIP into a fresh subdirectory of $DestinationRoot and returns the
        package root path (the folder containing app/, checksums.sha256, release-manifest.json).
        Fails closed when the ZIP is missing. Never modifies the source ZIP.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseZipPath,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    if (-not (Test-Path -LiteralPath $ReleaseZipPath -PathType Leaf)) {
        throw "Release ZIP was not found: $ReleaseZipPath"
    }

    $resolvedZip = (Resolve-Path -LiteralPath $ReleaseZipPath).Path

    # Fail closed on unsafe archives BEFORE extracting: reject absolute paths, parent-directory
    # traversal (..), and duplicate entries (defense in depth on top of ExtractToDirectory).
    Test-ReleaseZipEntry -ZipPath $resolvedZip | Out-Null

    # Use the .NET directory API for scratch directories so extraction is never suppressed by a
    # caller's -WhatIf (WhatIf must gate real install operations, not internal temp scratch).
    [System.IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
    $extractDir = Join-Path $DestinationRoot ("extract-" + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($extractDir) | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($resolvedZip, $extractDir)

    # The package root is the directory that contains release-manifest.json (top level, or a
    # single wrapper subdirectory if the ZIP was created with one).
    if (Test-Path -LiteralPath (Join-Path $extractDir 'release-manifest.json')) {
        return $extractDir
    }
    $child = @(Get-ChildItem -LiteralPath $extractDir -Directory)
    if ($child.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $child[0].FullName 'release-manifest.json'))) {
        return $child[0].FullName
    }
    throw "Extracted package does not contain release-manifest.json: $extractDir"
}

function Test-ReleasePackageIntegrity {
    <#
        Pre-install integrity gate for an extracted release package. Verifies the checksum
        manifest, reads the signing status, and scans the payload for forbidden secret files.
        Returns a structured result; Ok is $false (never throws for expected failures) so the
        caller can report Failed rather than crash. RequireSigned demands signingStatus=Signed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [switch]$RequireSigned,
        [string]$PayloadSubdirectory = 'app',
        [string[]]$AllowedTopLevel = @('app', 'checksums.sha256', 'release-manifest.json')
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path
    $manifestPath = Join-Path $root 'release-manifest.json'
    $messages = @()

    $metadata = $null
    $manifestPresent = Test-Path -LiteralPath $manifestPath -PathType Leaf
    $signingStatus = 'Unknown'
    if ($manifestPresent) {
        try {
            $metadata = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $signingStatus = [string]$metadata.signingStatus
        }
        catch {
            $manifestPresent = $false
            $messages += 'release-manifest.json is malformed.'
        }
    }
    else {
        $messages += 'release-manifest.json is missing.'
    }

    # No unexpected top-level entries (only app/, checksums.sha256, release-manifest.json).
    $unexpectedTop = @()
    foreach ($item in (Get-ChildItem -LiteralPath $root -Force)) {
        if ($AllowedTopLevel -notcontains $item.Name) { $unexpectedTop += $item.Name }
    }
    $topLevelOk = ($unexpectedTop.Count -eq 0)
    if (-not $topLevelOk) {
        $messages += "Unexpected top-level package entries: $($unexpectedTop -join ', ')"
    }

    # File-count consistency: manifest fileCount == checksum entries == actual payload files.
    $countOk = $true
    try {
        $actualPayloadCount = @(Get-ReleasePayloadFile -PackageRoot $root -PayloadSubdirectory $PayloadSubdirectory).Count
        $checksumCount = (Read-ReleaseChecksumManifest -ManifestPath (Join-Path $root 'checksums.sha256')).Count
        $manifestCount = if ($null -ne $metadata -and $null -ne $metadata.fileCount) { [int]$metadata.fileCount } else { -1 }
        if ($manifestCount -ne $actualPayloadCount -or $checksumCount -ne $actualPayloadCount) {
            $countOk = $false
            $messages += "File count mismatch: manifest=$manifestCount checksums=$checksumCount payload=$actualPayloadCount."
        }
    }
    catch {
        $countOk = $false
        $messages += "File-count verification failed: $($_.Exception.Message)"
    }

    $checksumOk = $false
    try {
        $checksum = Test-ReleaseChecksum -PackageRoot $root -PayloadSubdirectory $PayloadSubdirectory
        $checksumOk = $checksum.Ok
        if (-not $checksumOk) {
            $messages += "Checksum mismatch. Missing=$($checksum.Missing -join ',') Modified=$($checksum.Modified -join ',') Extra=$($checksum.Extra -join ',')"
        }
    }
    catch {
        $messages += "Checksum verification failed: $($_.Exception.Message)"
    }

    $secrets = @(Test-ReleaseSecretExclusion -Path (Join-Path $root $PayloadSubdirectory))
    if ($secrets.Count -gt 0) {
        $messages += "Forbidden secret files present: $($secrets -join ', ')"
    }

    $signingOk = $true
    if ($RequireSigned -and $signingStatus -ne 'Signed') {
        $signingOk = $false
        $messages += "RequireSigned: package signing status is '$signingStatus', not 'Signed'."
    }
    elseif ($signingStatus -ne 'Signed') {
        $messages += "UnsignedPilot package: signatures are not present. Acceptable only for controlled pilot use."
    }

    $ok = $manifestPresent -and $checksumOk -and ($secrets.Count -eq 0) -and $signingOk -and $topLevelOk -and $countOk
    return [pscustomobject]@{
        Ok               = $ok
        ManifestPresent  = $manifestPresent
        ChecksumOk       = $checksumOk
        TopLevelOk       = $topLevelOk
        CountOk          = $countOk
        SigningStatus    = $signingStatus
        SecretViolations = $secrets
        UnexpectedTop    = $unexpectedTop
        Messages         = $messages
    }
}

function Copy-ReleasePayloadWithRollback {
    <#
        Replaces the contents of $DestinationDir with the contents of $SourceDir, taking a
        snapshot first and restoring it if the copy fails. Config/logs living outside
        $DestinationDir are never touched. Returns $true on success; rethrows on failure
        after rolling back. -SimulateFailure is a test-only seam that forces a mid-swap
        failure to exercise the rollback path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationDir,
        [Parameter(Mandatory = $true)][string]$BackupRoot,
        [switch]$SimulateFailure
    )

    $source = (Resolve-Path -LiteralPath $SourceDir).Path
    if (-not (Test-Path -LiteralPath $DestinationDir)) {
        New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    }
    $dest = (Resolve-Path -LiteralPath $DestinationDir).Path
    if (-not (Test-Path -LiteralPath $BackupRoot)) {
        New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
    }

    $rollbackPath = $null
    $existing = @(Get-ChildItem -LiteralPath $dest -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        $rollbackPath = Join-Path $BackupRoot ("Program.rollback.{0:yyyyMMddHHmmssfff}" -f (Get-Date))
        New-Item -ItemType Directory -Force -Path $rollbackPath | Out-Null
        foreach ($item in $existing) {
            Copy-Item -LiteralPath $item.FullName -Destination $rollbackPath -Recurse -Force
        }
    }

    try {
        Get-ChildItem -LiteralPath $dest -Force | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
        }
        if ($SimulateFailure) { throw 'Simulated copy failure (test seam).' }
        Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force -ErrorAction Stop
        }
    }
    catch {
        if ($rollbackPath) {
            Get-ChildItem -LiteralPath $dest -Force -ErrorAction SilentlyContinue | ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
            Get-ChildItem -LiteralPath $rollbackPath -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force
            }
        }
        throw
    }

    if ($rollbackPath -and (Test-Path -LiteralPath $rollbackPath)) {
        Remove-Item -LiteralPath $rollbackPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $true
}

function Write-ReleaseMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $json = ($Metadata | ConvertTo-Json -Depth 6)
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
    return $Path
}

function Test-PathReparsePoint {
    <#
        Inspects each directory/file segment from the path up to the root to detect
        symbolic links, directory junctions, and other reparse points.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $curr = $Path
    while (-not [string]::IsNullOrEmpty($curr)) {
        if ([System.IO.File]::Exists($curr) -or [System.IO.Directory]::Exists($curr)) {
            $attr = [System.IO.File]::GetAttributes($curr)
            if (($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                return $true
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($curr)
        if ($parent -eq $curr -or [string]::IsNullOrEmpty($parent)) { break }
        $curr = $parent
    }
    return $false
}

function Test-CanonicalSafePath {
    <#
        Ensures $Path is canonical, strictly inside $ExpectedParentDirectory, and free
        of reparse points / symlinks / junctions.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParentDirectory
    )

    $canonicalParent = [System.IO.Path]::GetFullPath($ExpectedParentDirectory).TrimEnd('\', '/')
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $canonicalPath.StartsWith($canonicalParent + '\', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($canonicalPath, $canonicalParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes expected directory: '$canonicalPath' is not inside '$canonicalParent'"
    }
    if (Test-PathReparsePoint -Path $canonicalPath) {
        throw "Path involves a symbolic link, junction, or reparse point: '$canonicalPath'"
    }
    return $canonicalPath
}

function New-RetentionMarker {
    <#
        Creates a per-run GUID-named retention marker file in $TargetDirectory.
        Validates canonical path safety, refuses collisions, and returns path + SHA-256.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$TargetDirectory,
        [string]$Prefix = 'phase12-retention'
    )

    $canonicalDir = [System.IO.Path]::GetFullPath($TargetDirectory)
    if (-not (Test-Path -LiteralPath $canonicalDir)) {
        [System.IO.Directory]::CreateDirectory($canonicalDir) | Out-Null
    }
    if (Test-PathReparsePoint -Path $canonicalDir) {
        throw "Target directory involves a reparse point: $canonicalDir"
    }

    if ($Prefix -match '[\\/:\*\?"<>\|\.]' -or $Prefix -match '\.\.') {
        throw "Prefix contains invalid characters or traversal: '$Prefix'"
    }

    $guid = [guid]::NewGuid().ToString('N')
    $fileName = "{0}-{1}.marker" -f $Prefix, $guid
    $markerPath = [System.IO.Path]::Combine($canonicalDir, $fileName)
    $safeMarkerPath = Test-CanonicalSafePath -Path $markerPath -ExpectedParentDirectory $canonicalDir

    if ([System.IO.File]::Exists($safeMarkerPath) -or [System.IO.Directory]::Exists($safeMarkerPath)) {
        throw "Marker collision detected at: $safeMarkerPath"
    }

    # Safe random content: no secrets, no PII
    $content = "phase12-retention-marker-{0}-{1}-{2}" -f $guid, [datetime]::UtcNow.ToString('yyyyMMddHHmmssfff'), ([guid]::NewGuid().ToString('N'))
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $bytes = $utf8NoBom.GetBytes($content)

    $fs = [System.IO.FileStream]::new($markerPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $fs.Write($bytes, 0, $bytes.Length)
        $fs.Flush()
    }
    finally {
        $fs.Dispose()
    }

    $hash = Get-Sha256Hex -LiteralPath $markerPath
    return [pscustomobject]@{
        Path     = $markerPath
        FileName = $fileName
        Hash     = $hash
    }
}

function Test-RetentionMarker {
    <#
        Verifies that a retention marker exists, is inside the expected directory,
        has no reparse points, and matches its expected SHA-256 hash.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][string]$ExpectedHash,
        [Parameter(Mandatory = $true)][string]$ExpectedParentDirectory
    )

    $canonicalParent = [System.IO.Path]::GetFullPath($ExpectedParentDirectory).TrimEnd('\', '/')
    $canonicalPath = [System.IO.Path]::GetFullPath($MarkerPath)

    if (-not $canonicalPath.StartsWith($canonicalParent + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{
            Exists     = $false
            HashMatch  = $false
            ActualHash = $null
            Message    = "Marker path escapes expected directory: $canonicalPath"
        }
    }

    if (Test-PathReparsePoint -Path $canonicalPath) {
        return [pscustomobject]@{
            Exists     = $false
            HashMatch  = $false
            ActualHash = $null
            Message    = "Marker path involves a reparse point: $canonicalPath"
        }
    }

    if (-not (Test-Path -LiteralPath $canonicalPath -PathType Leaf)) {
        return [pscustomobject]@{
            Exists     = $false
            HashMatch  = $false
            ActualHash = $null
            Message    = "Marker file does not exist: $canonicalPath"
        }
    }

    $actualHash = Get-Sha256Hex -LiteralPath $canonicalPath
    $hashMatch = ($actualHash -eq $ExpectedHash)
    $msg = if ($hashMatch) { 'Marker intact (SHA-256 match).' } else { "Marker hash mismatch: expected $ExpectedHash, got $actualHash" }

    return [pscustomobject]@{
        Exists     = $true
        HashMatch  = $hashMatch
        ActualHash = $actualHash
        Message    = $msg
    }
}

function Remove-RetentionMarker {
    <#
        Deletes ONLY the exact marker file created by the run after validating
        path containment and absence of reparse points. Never uses wildcards.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][string]$ExpectedParentDirectory
    )

    if ([string]::IsNullOrWhiteSpace($MarkerPath)) { return }
    $canonicalParent = [System.IO.Path]::GetFullPath($ExpectedParentDirectory).TrimEnd('\', '/')
    $canonicalPath = [System.IO.Path]::GetFullPath($MarkerPath)

    if (-not $canonicalPath.StartsWith($canonicalParent + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete marker: path escapes expected directory: $canonicalPath"
    }

    if (Test-PathReparsePoint -Path $canonicalPath) {
        throw "Refusing to delete marker: path involves a reparse point: $canonicalPath"
    }

    if ([System.IO.File]::Exists($canonicalPath)) {
        [System.IO.File]::Delete($canonicalPath)
    }
}

function New-ToolingChecksumManifest {
    <#
        Computes SHA-256 for every file in the tooling bundle (except the manifest itself)
        and writes a deterministic TOOLING-SHA256.txt manifest.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [string]$ManifestName = 'TOOLING-SHA256.txt'
    )

    $root = (Resolve-Path -LiteralPath $BundleRoot).Path
    $files = Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.Name -ne $ManifestName }
    $hashes = @{}
    $seenLower = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($file in $files) {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $file.FullName
        if ([System.IO.Path]::IsPathRooted($rel) -or $rel -match '^[A-Za-z]:' -or $rel.StartsWith('/') -or ($rel -split '/') -contains '..') {
            throw "Unsafe path detected in bundle: $rel"
        }
        if (-not $seenLower.Add($rel)) {
            throw "Duplicate or normalized-collision entry in bundle: $rel"
        }
        $hashes[$rel] = Get-Sha256Hex -LiteralPath $file.FullName
    }

    $builder = [System.Text.StringBuilder]::new()
    foreach ($key in (Get-OrdinalSortedString -Value @($hashes.Keys))) {
        [void]$builder.Append($hashes[$key])
        [void]$builder.Append('  ')
        [void]$builder.Append($key)
        [void]$builder.Append("`n")
    }

    $manifestPath = Join-Path $root $ManifestName
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($manifestPath, $builder.ToString(), $utf8NoBom)
    return $manifestPath
}

function Test-ToolingChecksumManifest {
    <#
        Validates that TOOLING-SHA256.txt covers the exact complete bundle file set
        (excluding only the manifest itself). Rejects missing, extra, duplicate,
        absolute, or parent-relative paths.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [string]$ManifestName = 'TOOLING-SHA256.txt'
    )

    $root = (Resolve-Path -LiteralPath $BundleRoot).Path
    $manifestPath = Join-Path $root $ManifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            Ok       = $false
            Missing  = @()
            Modified = @()
            Extra    = @()
            Messages = @("Tooling manifest not found: $manifestPath")
        }
    }

    $expected = @{}
    $seenLower = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $lines = [System.IO.File]::ReadAllLines($manifestPath)
    $messages = @()

    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64})\s{1,}(.+?)\s*$')
        if (-not $match.Success) {
            $messages += "Malformed tooling manifest line: $line"
            continue
        }
        $relative = $match.Groups[2].Value.Trim()
        $norm = $relative -replace '\\', '/'
        if ([System.IO.Path]::IsPathRooted($norm) -or $norm -match '^[A-Za-z]:' -or $norm.StartsWith('/') -or ($norm -split '/') -contains '..') {
            $messages += "Unsafe or traversal path in tooling manifest: $norm"
            continue
        }
        if (-not $seenLower.Add($norm)) {
            $messages += "Duplicate or normalized collision in tooling manifest: $norm"
            continue
        }
        $expected[$norm] = $match.Groups[1].Value.ToUpperInvariant()
    }

    $actual = @{}
    $actualSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.Name -ne $ManifestName })) {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $file.FullName
        if (-not $actualSeen.Add($rel)) {
            $messages += "Duplicate or normalized collision on filesystem: $rel"
        }
        $actual[$rel] = Get-Sha256Hex -LiteralPath $file.FullName
    }

    $missing = @()
    $modified = @()
    foreach ($rel in $expected.Keys) {
        if (-not $actual.ContainsKey($rel)) {
            $missing += $rel
        }
        elseif ($actual[$rel] -ne $expected[$rel]) {
            $modified += $rel
        }
    }

    $extra = @()
    foreach ($rel in $actual.Keys) {
        if (-not $expected.ContainsKey($rel)) {
            $extra += $rel
        }
    }

    $missing = @(Get-OrdinalSortedString -Value @($missing))
    $modified = @(Get-OrdinalSortedString -Value @($modified))
    $extra = @(Get-OrdinalSortedString -Value @($extra))

    $ok = ($messages.Count -eq 0 -and $missing.Count -eq 0 -and $modified.Count -eq 0 -and $extra.Count -eq 0)
    return [pscustomobject]@{
        Ok       = $ok
        Missing  = $missing
        Modified = $modified
        Extra    = $extra
        Messages = $messages
    }
}

Export-ModuleMember -Function `
    Get-ReleaseSecretPattern, `
    Get-OrdinalSortedString, `
    Get-ReleaseRelativePath, `
    Get-ReleaseFileInventory, `
    Test-ReleaseSecretExclusion, `
    Get-ReleasePayloadFile, `
    New-ReleaseChecksumManifest, `
    Read-ReleaseChecksumManifest, `
    Test-ReleaseChecksum, `
    Test-CodeSigningCertificate, `
    Test-ReleaseZipEntry, `
    Expand-ReleasePackage, `
    Test-ReleasePackageIntegrity, `
    Copy-ReleasePayloadWithRollback, `
    New-ReleaseMetadata, `
    Write-ReleaseMetadata, `
    Test-PathReparsePoint, `
    Test-CanonicalSafePath, `
    New-RetentionMarker, `
    Test-RetentionMarker, `
    Remove-RetentionMarker, `
    New-ToolingChecksumManifest, `
    Test-ToolingChecksumManifest
