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
        $hashes[$rel] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
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
        $actual[$rel] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
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
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
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
    Copy-ReleasePayloadWithRollback, `
    New-ReleaseMetadata, `
    Write-ReleaseMetadata
