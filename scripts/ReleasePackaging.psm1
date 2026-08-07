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

# Authenticode digest algorithm OIDs, read out of the embedded PKCS#7 SignerInfo. SHA-1 is
# recorded so a weak signature can be reported and rejected rather than silently accepted.
$script:AuthenticodeDigestOid = @{
    '1.3.14.3.2.26'           = 'SHA1'
    '2.16.840.1.101.3.4.2.1'  = 'SHA256'
    '2.16.840.1.101.3.4.2.2'  = 'SHA384'
    '2.16.840.1.101.3.4.2.3'  = 'SHA512'
}

# Unsigned-attribute OIDs that carry a timestamp. RFC 3161 (szOID_RFC3161_counterSign) is the
# production requirement; the legacy Authenticode counter-signature is recognised so a legacy-only
# timestamp can be reported and rejected instead of passing as "timestamped".
$script:Rfc3161CounterSignOid = '1.3.6.1.4.1.311.3.3.1'
$script:LegacyCounterSignOid = '1.2.840.113549.1.9.6'

# Configuration keys and signtool arguments that would carry a credential. Present so the signing
# configuration and any caller-supplied signtool arguments fail closed instead of leaking a PIN or
# password into source control, logs, command history, or release evidence.
$script:ForbiddenSigningConfigKeyPattern = '(?i)(password|passwd|pwd|passphrase|\bpin\b|secret|credential|apikey|api_key|accesskey|token)'
$script:ForbiddenSignToolArgument = @('/p', '-p', '/password', '--password', '/pin', '-pin', '/kp', '/csppin', '/du')

# Required signing-configuration values. A value left as an unresolved <PLACEHOLDER> is rejected.
$script:RequiredSigningConfigField = @('certificateThumbprint', 'timestampServerUrl')

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
        not-yet-valid, expired, too close to expiry, or a signer identity that does not
        match the expected subject/thumbprint. Rejects HTTPS/Server-Authentication
        certificates because they lack the Code Signing EKU.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [datetime]$Now = (Get-Date),
        [string]$ExpectedSubject,
        [string]$ExpectedThumbprint,
        [string]$ExpectedIssuer,
        [int]$MinimumRemainingDays = 0
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

    # Signer identity: refuse to sign with a certificate other than the one the release was
    # authorized to use. Comparison is on the exact subject DN / thumbprint from configuration.
    if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
        $expected = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()
        $actual = ($Certificate.Thumbprint -replace '\s', '').ToUpperInvariant()
        if ($actual -ne $expected) {
            throw "Signer mismatch: certificate thumbprint '$actual' does not match the expected thumbprint '$expected'."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSubject) -and
        -not [string]::Equals($Certificate.Subject.Trim(), $ExpectedSubject.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Signer mismatch: certificate subject '$($Certificate.Subject)' does not match the expected subject '$ExpectedSubject'."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedIssuer) -and
        -not [string]::Equals($Certificate.Issuer.Trim(), $ExpectedIssuer.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Signer mismatch: certificate issuer '$($Certificate.Issuer)' does not match the expected issuer '$ExpectedIssuer'."
    }

    # Renewal guard: stop a release that would be signed with a certificate about to expire.
    if ($MinimumRemainingDays -gt 0) {
        $remaining = ($Certificate.NotAfter - $Now).TotalDays
        if ($remaining -lt $MinimumRemainingDays) {
            throw ("Certificate expires in {0:N1} day(s), which is below the required minimum of {1} day(s). Renew the certificate before signing." -f $remaining, $MinimumRemainingDays)
        }
    }

    return $true
}

function Get-ReleaseSigningPolicy {
    <#
        Loads the signing allowlist that decides which payload files may contain executable
        content and which of them must carry a release signature. Fails closed when the file is
        missing, malformed, or does not declare requiredSigned / executableExtensions.
    #>
    [CmdletBinding()]
    param(
        [string]$PolicyPath = (Join-Path $PSScriptRoot 'signing-allowlist.json')
    )

    if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
        throw "Signing allowlist was not found: $PolicyPath"
    }

    try {
        $raw = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Signing allowlist is malformed JSON ($PolicyPath): $($_.Exception.Message)"
    }

    function Get-PolicyList([object]$Source, [string]$Name, [bool]$Mandatory) {
        if ($Source.PSObject.Properties.Name -notcontains $Name) {
            if ($Mandatory) { throw "Signing allowlist is missing the required '$Name' array." }
            return @()
        }
        return @($Source.$Name)
    }

    $required = @(Get-PolicyList $raw 'requiredSigned' $true)
    $executable = @(Get-PolicyList $raw 'executableExtensions' $true)
    if ($required.Count -eq 0) {
        throw 'Signing allowlist declares no requiredSigned entries; refusing to sign a release with nothing required.'
    }
    if ($executable.Count -eq 0) {
        throw 'Signing allowlist declares no executableExtensions; unexpected executable content could not be detected.'
    }

    return [pscustomobject]@{
        PolicyPath              = (Resolve-Path -LiteralPath $PolicyPath).Path
        RequiredSigned          = $required
        OptionalSigned          = @(Get-PolicyList $raw 'optionalSigned' $false)
        AllowedThirdPartySigned = @(Get-PolicyList $raw 'allowedThirdPartySigned' $false)
        AllowedUnsigned         = @(Get-PolicyList $raw 'allowedUnsigned' $false)
        ExecutableExtensions    = @($executable | ForEach-Object { $_.ToLowerInvariant() })
    }
}

function Test-ReleasePathPattern {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [AllowNull()][AllowEmptyCollection()][string[]]$Pattern
    )

    if ($null -eq $Pattern) { return $false }
    foreach ($p in $Pattern) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        if ($RelativePath -like $p) { return $true }
    }
    return $false
}

function Resolve-ReleaseSigningPlan {
    <#
        Classifies every payload file against the signing allowlist and returns the plan:
        which files must be signed by the release signer, which may be, which executable content
        is allowed to carry a third-party signature or no signature, and which executable content
        is unexpected. Also reports literal requiredSigned entries that are absent from the payload.

        Never throws for a policy violation: the caller decides whether to reject, so the full set
        of problems can be reported in one pass.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [object]$Policy,
        [string]$PayloadSubdirectory = 'app'
    )

    if ($null -eq $Policy) { $Policy = Get-ReleaseSigningPolicy }
    $root = (Resolve-Path -LiteralPath $PackageRoot).Path

    $required = @()
    $optional = @()
    $thirdParty = @()
    $allowedUnsigned = @()
    $unexpected = @()

    foreach ($file in (Get-ReleasePayloadFile -PackageRoot $root -PayloadSubdirectory $PayloadSubdirectory)) {
        $rel = Get-ReleaseRelativePath -Root $root -FullName $file.FullName
        $entry = [pscustomobject]@{ RelativePath = $rel; FullName = $file.FullName }

        if (Test-ReleasePathPattern -RelativePath $rel -Pattern $Policy.RequiredSigned) { $required += $entry; continue }
        if (Test-ReleasePathPattern -RelativePath $rel -Pattern $Policy.OptionalSigned) { $optional += $entry; continue }
        if (Test-ReleasePathPattern -RelativePath $rel -Pattern $Policy.AllowedThirdPartySigned) { $thirdParty += $entry; continue }
        if (Test-ReleasePathPattern -RelativePath $rel -Pattern $Policy.AllowedUnsigned) { $allowedUnsigned += $entry; continue }

        $extension = [System.IO.Path]::GetExtension($rel)
        if (-not [string]::IsNullOrEmpty($extension) -and $Policy.ExecutableExtensions -contains $extension.ToLowerInvariant()) {
            $unexpected += $entry
        }
    }

    # Literal (wildcard-free) requiredSigned entries name a file that must exist.
    $present = @($required | ForEach-Object { $_.RelativePath })
    $missingRequired = @()
    foreach ($pattern in $Policy.RequiredSigned) {
        if ($pattern -match '[\*\?\[]') { continue }
        if ($present -notcontains $pattern) { $missingRequired += $pattern }
    }

    $messages = @()
    foreach ($m in $missingRequired) { $messages += "Required signed file is missing from the payload: $m" }
    foreach ($u in $unexpected) { $messages += "Unexpected executable content (not in the signing allowlist): $($u.RelativePath)" }

    return [pscustomobject]@{
        Ok                      = ($missingRequired.Count -eq 0 -and $unexpected.Count -eq 0)
        Required                = $required
        Optional                = $optional
        SignTargets             = @($required + $optional)
        AllowedThirdPartySigned = $thirdParty
        AllowedUnsigned         = $allowedUnsigned
        UnexpectedExecutables   = $unexpected
        MissingRequired         = $missingRequired
        Messages                = $messages
        PolicyPath              = $Policy.PolicyPath
    }
}

function Get-AuthenticodeSignatureBlob {
    <#
        Extracts the raw embedded PKCS#7 signature from a PE image (certificate table, data
        directory index 4) or from the "# SIG # Begin signature block" block of a script file.
        Returns $null when the file carries no embedded signature.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $bytes = [System.IO.File]::ReadAllBytes($LiteralPath)
    if ($bytes.Length -lt 2) { return $null }

    if ($bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A) {
        if ($bytes.Length -lt 0x40) { return $null }
        $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -le 0 -or ($peOffset + 26) -ge $bytes.Length) { return $null }
        if (-not ($bytes[$peOffset] -eq 0x50 -and $bytes[$peOffset + 1] -eq 0x45)) { return $null }

        $optionalHeader = $peOffset + 24
        $magic = [System.BitConverter]::ToUInt16($bytes, $optionalHeader)
        $dataDirectoryOffset = -1
        if ($magic -eq 0x20B) { $dataDirectoryOffset = $optionalHeader + 112 }   # PE32+
        elseif ($magic -eq 0x10B) { $dataDirectoryOffset = $optionalHeader + 96 } # PE32
        if ($dataDirectoryOffset -lt 0) { return $null }

        # IMAGE_DIRECTORY_ENTRY_SECURITY is index 4; for it the "VirtualAddress" is a file offset.
        $securityEntry = $dataDirectoryOffset + (4 * 8)
        if (($securityEntry + 8) -gt $bytes.Length) { return $null }
        $certOffset = [System.BitConverter]::ToInt32($bytes, $securityEntry)
        $certSize = [System.BitConverter]::ToInt32($bytes, $securityEntry + 4)
        if ($certOffset -le 0 -or $certSize -le 8 -or ($certOffset + $certSize) -gt $bytes.Length) { return $null }

        # WIN_CERTIFICATE { DWORD dwLength; WORD wRevision; WORD wCertificateType; BYTE bCertificate[] }
        $declaredLength = [System.BitConverter]::ToInt32($bytes, $certOffset)
        $blobLength = ([Math]::Min($declaredLength, $certSize)) - 8
        if ($blobLength -le 0) { return $null }
        $blob = New-Object byte[] $blobLength
        [System.Array]::Copy($bytes, $certOffset + 8, $blob, 0, $blobLength)
        return , $blob
    }

    $text = [System.IO.File]::ReadAllText($LiteralPath)
    $match = [regex]::Match($text, '(?ms)^#\s*SIG\s*#\s*Begin signature block\r?$(.*?)^#\s*SIG\s*#\s*End signature block')
    if (-not $match.Success) { return $null }
    $base64 = (($match.Groups[1].Value -split "`n") | ForEach-Object { ($_ -replace '^\s*#\s*', '').Trim() }) -join ''
    if ([string]::IsNullOrWhiteSpace($base64)) { return $null }
    try { return , ([System.Convert]::FromBase64String($base64)) } catch { return $null }
}

function Get-AuthenticodeSignatureDetail {
    <#
        Reports the facts Get-AuthenticodeSignature does not expose: the Authenticode digest
        algorithm actually used, and whether the timestamp is an RFC 3161 timestamp or the legacy
        Authenticode counter-signature. Both are read out of the embedded PKCS#7 SignerInfo.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $result = [pscustomobject]@{
        HasSignature       = $false
        DigestAlgorithm    = 'None'
        DigestAlgorithmOid = $null
        Timestamped        = $false
        TimestampKind      = 'None'
        SignerThumbprint   = $null
        SignerCertificate  = $null
        SignatureIntact    = $false
    }

    $blob = Get-AuthenticodeSignatureBlob -LiteralPath $LiteralPath
    if ($null -eq $blob -or $blob.Length -eq 0) { return $result }

    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue | Out-Null
    $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
    try { $cms.Decode($blob) } catch { return $result }
    if ($cms.SignerInfos.Count -eq 0) { return $result }

    $signer = $cms.SignerInfos[0]
    $result.HasSignature = $true
    $oid = $signer.DigestAlgorithm.Value
    $result.DigestAlgorithmOid = $oid
    $result.DigestAlgorithm = if ($script:AuthenticodeDigestOid.ContainsKey($oid)) { $script:AuthenticodeDigestOid[$oid] } else { $oid }
    if ($null -ne $signer.Certificate) {
        $result.SignerThumbprint = $signer.Certificate.Thumbprint
        $result.SignerCertificate = $signer.Certificate
    }

    # Cryptographic check of the embedded signature itself (signature over the signed attributes),
    # independent of whether Windows happens to resolve this file through a security catalog.
    try {
        $cms.CheckSignature($true)
        $result.SignatureIntact = $true
    }
    catch {
        $result.SignatureIntact = $false
    }

    foreach ($attribute in $signer.UnsignedAttributes) {
        if ($attribute.Oid.Value -eq $script:Rfc3161CounterSignOid) {
            $result.Timestamped = $true
            $result.TimestampKind = 'RFC3161'
        }
        elseif ($attribute.Oid.Value -eq $script:LegacyCounterSignOid -and $result.TimestampKind -eq 'None') {
            $result.Timestamped = $true
            $result.TimestampKind = 'Legacy'
        }
    }
    return $result
}

function Test-ReleaseSignatureFile {
    <#
        Verifies one file's Authenticode signature against the release requirements and returns a
        structured result. Ok is $false (with Reasons) for: tamper (HashMismatch), unsigned,
        untrusted chain when required, signer mismatch, a weaker-than-required digest algorithm,
        a missing timestamp, or a legacy timestamp when RFC 3161 is required.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [string]$RelativePath,
        [string]$ExpectedThumbprint,
        [string]$ExpectedSubject,
        [string]$RequiredDigestAlgorithm = 'SHA256',
        [switch]$RequireTimestamp,
        [switch]$RequireRfc3161Timestamp,
        [switch]$RequireTrustedChain
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) { $RelativePath = $LiteralPath }
    $reasons = @()

    $osSignature = Get-AuthenticodeSignature -LiteralPath $LiteralPath
    $detail = Get-AuthenticodeSignatureDetail -LiteralPath $LiteralPath
    $status = [string]$osSignature.Status

    # Identity comes from the EMBEDDED signature, never from Get-AuthenticodeSignature's signer.
    # Windows resolves a file through a security catalog when one matches, and then reports the
    # catalog's signer even though that signer never signed this file. A release binary must carry
    # its own embedded signature, and that is the signature this check authenticates.
    $signer = $detail.SignerCertificate
    $hasSigner = ($null -ne $signer)

    $signed = $false
    if ($status -eq 'HashMismatch') {
        $reasons += 'File was modified after signing (Authenticode HashMismatch).'
    }
    elseif (-not $detail.HasSignature -or -not $hasSigner) {
        if ($status -eq 'Valid') {
            $reasons += 'File has no embedded Authenticode signature (Windows reports it as catalog-signed, which is not acceptable for a release binary).'
        }
        else {
            $reasons += 'File is not Authenticode signed.'
        }
    }
    elseif (-not $detail.SignatureIntact) {
        $reasons += 'Embedded Authenticode signature failed its cryptographic check.'
    }
    else {
        $signed = $true

        $osThumbprint = if ($null -ne $osSignature.SignerCertificate) { $osSignature.SignerCertificate.Thumbprint } else { $null }
        if ($null -ne $osThumbprint -and $osThumbprint -ne $signer.Thumbprint) {
            $reasons += "Windows reports this file as signed by '$($osSignature.SignerCertificate.Subject)' (catalog), which is not the embedded signer."
        }

        # Chain trust is evaluated on the embedded signer certificate. A failure usually means the
        # signing CA is not installed on this verification machine, which is a target-machine trust
        # concern, so it only fails the file when the caller demands a trusted chain.
        if ($RequireTrustedChain) {
            $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
            try {
                $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
                $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
                [void]$chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new($script:CodeSigningEku))
                if (-not $chain.Build($signer)) {
                    $chainStatus = @($chain.ChainStatus | ForEach-Object { $_.Status }) -join ', '
                    $reasons += "Signing certificate does not chain to a trusted root on this machine ($chainStatus)."
                }
            }
            finally {
                $chain.Dispose()
            }
        }
    }

    if ($signed) {
        if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
            $expected = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()
            $actual = ($signer.Thumbprint -replace '\s', '').ToUpperInvariant()
            if ($actual -ne $expected) {
                $reasons += "Signer mismatch: signed by thumbprint '$actual', expected '$expected'."
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedSubject) -and
            -not [string]::Equals($signer.Subject.Trim(), $ExpectedSubject.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
            $reasons += "Signer mismatch: signed by '$($signer.Subject)', expected '$ExpectedSubject'."
        }
        if (-not [string]::IsNullOrWhiteSpace($RequiredDigestAlgorithm) -and
            -not [string]::Equals($detail.DigestAlgorithm, $RequiredDigestAlgorithm, [System.StringComparison]::OrdinalIgnoreCase)) {
            $reasons += "Signature digest algorithm is '$($detail.DigestAlgorithm)', expected '$RequiredDigestAlgorithm'."
        }
        if ($RequireTimestamp -and -not $detail.Timestamped) {
            $reasons += 'Signature has no timestamp.'
        }
        if ($RequireRfc3161Timestamp -and $detail.TimestampKind -ne 'RFC3161') {
            $reasons += "Signature timestamp is '$($detail.TimestampKind)', an RFC 3161 timestamp is required."
        }
    }

    return [pscustomobject]@{
        Ok               = ($signed -and $reasons.Count -eq 0)
        RelativePath     = $RelativePath
        Signed           = $signed
        Tampered         = ($status -eq 'HashMismatch')
        Status           = $status
        SignerSubject    = if ($hasSigner) { $signer.Subject } else { $null }
        SignerIssuer     = if ($hasSigner) { $signer.Issuer } else { $null }
        SignerThumbprint = if ($hasSigner) { $signer.Thumbprint } else { $null }
        NotBeforeUtc     = if ($hasSigner) { $signer.NotBefore.ToUniversalTime().ToString('o') } else { $null }
        NotAfterUtc      = if ($hasSigner) { $signer.NotAfter.ToUniversalTime().ToString('o') } else { $null }
        DigestAlgorithm  = $detail.DigestAlgorithm
        Timestamped      = $detail.Timestamped
        TimestampKind    = $detail.TimestampKind
        Reasons          = $reasons
    }
}

function New-ReleaseSigningReport {
    <#
        Verifies a whole package against the signing allowlist and the release signing
        requirements, and returns the evidence that release-manifest.json records. Ok is $false
        when any required file fails, when required files are missing, or when the payload
        contains unexpected executable content.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [object]$Policy,
        [string]$PayloadSubdirectory = 'app',
        [string]$ExpectedThumbprint,
        [string]$ExpectedSubject,
        [string]$RequiredDigestAlgorithm = 'SHA256',
        [string]$TimestampServerUrl,
        [switch]$RequireTimestamp,
        [switch]$RequireRfc3161Timestamp,
        [switch]$RequireTrustedChain
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path
    $plan = Resolve-ReleaseSigningPlan -PackageRoot $root -Policy $Policy -PayloadSubdirectory $PayloadSubdirectory
    $messages = @($plan.Messages)

    $fileResults = @()
    foreach ($target in $plan.SignTargets) {
        $fileResults += Test-ReleaseSignatureFile -LiteralPath $target.FullName -RelativePath $target.RelativePath `
            -ExpectedThumbprint $ExpectedThumbprint -ExpectedSubject $ExpectedSubject `
            -RequiredDigestAlgorithm $RequiredDigestAlgorithm `
            -RequireTimestamp:$RequireTimestamp -RequireRfc3161Timestamp:$RequireRfc3161Timestamp `
            -RequireTrustedChain:$RequireTrustedChain
    }

    # Third-party executable content must still carry some signature, but not ours.
    foreach ($target in $plan.AllowedThirdPartySigned) {
        $r = Test-ReleaseSignatureFile -LiteralPath $target.FullName -RelativePath $target.RelativePath `
            -RequiredDigestAlgorithm '' -RequireTrustedChain:$RequireTrustedChain
        if (-not $r.Signed) { $messages += "Allowed third-party file is unsigned: $($target.RelativePath)" }
        if ($r.Tampered) { $messages += "Allowed third-party file failed integrity: $($target.RelativePath)" }
    }

    $failed = @($fileResults | Where-Object { -not $_.Ok })
    foreach ($f in $failed) {
        $messages += ("Signature verification failed for {0}: {1}" -f $f.RelativePath, ($f.Reasons -join ' '))
    }
    $thirdPartyProblems = @($messages | Where-Object { $_ -like 'Allowed third-party file*' })

    $signedCount = @($fileResults | Where-Object { $_.Signed }).Count
    $primary = @($fileResults | Where-Object { $_.Signed } | Select-Object -First 1)
    $signer = if ($primary.Count -gt 0) { $primary[0] } else { $null }

    $ok = ($plan.Ok -and $failed.Count -eq 0 -and $thirdPartyProblems.Count -eq 0 -and $fileResults.Count -gt 0)

    return [pscustomobject]@{
        Ok                    = $ok
        VerificationResult    = if ($ok) { 'Passed' } else { 'Failed' }
        VerifiedAtUtc         = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        RequiredFileCount     = $plan.Required.Count
        SignTargetCount       = $plan.SignTargets.Count
        SignedFileCount       = $signedCount
        SignerSubject         = if ($signer) { $signer.SignerSubject } else { $null }
        SignerIssuer          = if ($signer) { $signer.SignerIssuer } else { $null }
        CertificateThumbprint = if ($signer) { $signer.SignerThumbprint } else { $null }
        SignatureAlgorithm    = if ($signer) { $signer.DigestAlgorithm } else { $null }
        Timestamped           = if ($signer) { [bool]$signer.Timestamped } else { $false }
        TimestampKind         = if ($signer) { $signer.TimestampKind } else { 'None' }
        TimestampAuthority    = $TimestampServerUrl
        CertificateNotBefore  = if ($signer) { $signer.NotBeforeUtc } else { $null }
        CertificateNotAfter   = if ($signer) { $signer.NotAfterUtc } else { $null }
        UnexpectedExecutables = @($plan.UnexpectedExecutables | ForEach-Object { $_.RelativePath })
        MissingRequired       = $plan.MissingRequired
        Files                 = $fileResults
        Messages              = $messages
        PolicyPath            = $plan.PolicyPath
    }
}

function New-ReleaseSigningOption {
    <#
        Builds the validated signing options from a signing-config JSON file plus explicit
        overrides. Fails closed when: the file is malformed, a required value is still an
        unresolved <PLACEHOLDER>, a secret-looking key is present, or a signtool argument would
        carry a credential. The returned object never holds a secret.
    #>
    [CmdletBinding()]
    param(
        [string]$ConfigPath,
        [string]$CertificateThumbprint,
        [string]$ExpectedSignerSubject,
        [string]$ExpectedSignerIssuer,
        [string]$TimestampServerUrl,
        [string]$StoreLocation,
        [string]$SignToolPath,
        [string]$AllowlistPath,
        [Nullable[bool]]$RequireRfc3161Timestamp,
        [Nullable[bool]]$RequireTrustedChain
    )

    $config = $null
    $configDir = $PSScriptRoot
    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
            throw "Signing configuration was not found: $ConfigPath"
        }
        $resolvedConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
        $configDir = [System.IO.Path]::GetDirectoryName($resolvedConfig)
        try { $config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json }
        catch { throw "Signing configuration is malformed JSON ($resolvedConfig): $($_.Exception.Message)" }

        # No credential may ever live in the signing configuration.
        foreach ($property in $config.PSObject.Properties) {
            if ($property.Name -match $script:ForbiddenSigningConfigKeyPattern) {
                throw "Signing configuration contains a forbidden secret-bearing key '$($property.Name)'. PINs, passwords, and tokens must never be stored in configuration."
            }
        }
    }

    function Get-ConfigValue([string]$Name, $Override, $Default) {
        if ($null -ne $Override -and -not ($Override -is [string] -and [string]::IsNullOrWhiteSpace($Override))) { return $Override }
        if ($null -ne $config -and $config.PSObject.Properties.Name -contains $Name) {
            $value = $config.$Name
            if ($null -ne $value -and -not ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) { return $value }
        }
        return $Default
    }

    $options = [pscustomobject]@{
        ConfigPath               = if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $null } else { (Resolve-Path -LiteralPath $ConfigPath).Path }
        Backend                  = [string](Get-ConfigValue 'backend' $null 'SignTool')
        CertificateSource        = [string](Get-ConfigValue 'certificateSource' $null 'Store')
        StoreLocation            = [string](Get-ConfigValue 'storeLocation' $StoreLocation 'LocalMachine')
        CertificateThumbprint    = [string](Get-ConfigValue 'certificateThumbprint' $CertificateThumbprint '')
        ExpectedSignerSubject    = [string](Get-ConfigValue 'expectedSignerSubject' $ExpectedSignerSubject '')
        ExpectedSignerIssuer     = [string](Get-ConfigValue 'expectedSignerIssuer' $ExpectedSignerIssuer '')
        TimestampServerUrl       = [string](Get-ConfigValue 'timestampServerUrl' $TimestampServerUrl '')
        TimestampDigestAlgorithm = [string](Get-ConfigValue 'timestampDigestAlgorithm' $null 'SHA256')
        FileDigestAlgorithm      = [string](Get-ConfigValue 'fileDigestAlgorithm' $null 'SHA256')
        RequireRfc3161Timestamp  = [bool](Get-ConfigValue 'requireRfc3161Timestamp' $RequireRfc3161Timestamp $true)
        RequireTrustedChain      = [bool](Get-ConfigValue 'requireTrustedChain' $RequireTrustedChain $true)
        MinimumRemainingDays     = [int](Get-ConfigValue 'minimumCertificateRemainingDays' $null 0)
        SignToolPath             = [string](Get-ConfigValue 'signToolPath' $SignToolPath '')
        AdditionalSignToolArgs   = @(Get-ConfigValue 'additionalSignToolArguments' $null @())
        AllowlistPath            = [string](Get-ConfigValue 'allowlistPath' $AllowlistPath 'signing-allowlist.json')
    }

    # An unresolved <PLACEHOLDER> means procurement is not finished; never sign with one.
    foreach ($field in $script:RequiredSigningConfigField) {
        $value = switch ($field) {
            'certificateThumbprint' { $options.CertificateThumbprint }
            'timestampServerUrl' { $options.TimestampServerUrl }
            default { '' }
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "Signing option '$field' is not set. Supply it in the signing configuration or as a parameter."
        }
        if ($value -match '^\s*<.*>\s*$') {
            throw "Signing option '$field' is still the unresolved placeholder '$value'. Fill in the confirmed value from the certificate provider before signing."
        }
    }
    foreach ($optionalPlaceholder in @('ExpectedSignerSubject', 'ExpectedSignerIssuer')) {
        if ($options.$optionalPlaceholder -match '^\s*<.*>\s*$') { $options.$optionalPlaceholder = '' }
    }
    if ($options.SignToolPath -match '^\s*<.*>\s*$') { $options.SignToolPath = '' }

    Test-SignToolArgumentSafety -Argument $options.AdditionalSignToolArgs | Out-Null

    if (-not [System.IO.Path]::IsPathRooted($options.AllowlistPath)) {
        $options.AllowlistPath = [System.IO.Path]::GetFullPath((Join-Path $configDir $options.AllowlistPath))
    }
    if (-not (Test-Path -LiteralPath $options.AllowlistPath -PathType Leaf)) {
        # Fall back to the allowlist shipped alongside the scripts.
        $options.AllowlistPath = Join-Path $PSScriptRoot 'signing-allowlist.json'
    }

    return $options
}

function Test-SignToolArgumentSafety {
    <#
        Rejects signtool arguments that would carry a credential. Keeping PINs and passwords out
        of the argument vector keeps them out of logs, process listings, and command history.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyCollection()][string[]]$Argument)

    if ($null -eq $Argument) { return $true }
    foreach ($arg in $Argument) {
        if ([string]::IsNullOrWhiteSpace($arg)) { continue }
        $trimmed = $arg.Trim()
        if ($script:ForbiddenSignToolArgument -contains $trimmed.ToLowerInvariant()) {
            throw "Refusing to sign: signtool argument '$trimmed' carries a credential. PINs and passwords must be supplied interactively by the authorized signer, never on the command line."
        }
        if ($trimmed -match $script:ForbiddenSigningConfigKeyPattern) {
            throw "Refusing to sign: signtool argument '$trimmed' looks like it embeds a credential."
        }
    }
    return $true
}

function Get-SignToolPath {
    <#
        Locates signtool.exe (explicit path, PATH, then the Windows 10/11 SDK bin folders).
        Returns $null when it cannot be found; the caller decides whether that is fatal.
        signtool is required for production because Set-AuthenticodeSignature applies the legacy
        Authenticode timestamp, not an RFC 3161 timestamp.
    #>
    [CmdletBinding()]
    param([string]$SignToolPath)

    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
            throw "signtool.exe was not found at the configured path: $SignToolPath"
        }
        return (Resolve-Path -LiteralPath $SignToolPath).Path
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @()
    foreach ($programFiles in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ([string]::IsNullOrWhiteSpace($programFiles)) { continue }
        $binRoot = Join-Path $programFiles 'Windows Kits\10\bin'
        if (-not (Test-Path -LiteralPath $binRoot)) { continue }
        foreach ($versionDir in (Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue)) {
            foreach ($arch in @('x64', 'x86')) {
                $candidate = Join-Path $versionDir.FullName "$arch\signtool.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidates += $candidate }
            }
        }
    }
    if ($candidates.Count -eq 0) { return $null }
    return (@(Get-OrdinalSortedString -Value $candidates))[-1]
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
        [string]$TimestampServer,
        # Evidence from New-ReleaseSigningReport. When supplied it is the authoritative source of
        # the signing block; the individual certificate parameters above remain for callers that
        # only have a subject/thumbprint to record.
        [object]$SigningReport
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
        # Non-secret release evidence only: no PIN, password, key material, or file path from the
        # signing workstation is ever recorded here.
        if ($null -ne $SigningReport) {
            $metadata['signing'] = [ordered]@{
                signerSubject         = $SigningReport.SignerSubject
                signerIssuer          = $SigningReport.SignerIssuer
                certificateSubject    = $SigningReport.SignerSubject
                certificateThumbprint = $SigningReport.CertificateThumbprint
                signatureAlgorithm    = $SigningReport.SignatureAlgorithm
                timestamped           = [bool]$SigningReport.Timestamped
                timestampKind         = $SigningReport.TimestampKind
                timestampServer       = if ([string]::IsNullOrWhiteSpace($SigningReport.TimestampAuthority)) { $TimestampServer } else { $SigningReport.TimestampAuthority }
                certificateValidity   = [ordered]@{
                    notBeforeUtc = $SigningReport.CertificateNotBefore
                    notAfterUtc  = $SigningReport.CertificateNotAfter
                }
                verification          = [ordered]@{
                    result            = $SigningReport.VerificationResult
                    verifiedAtUtc     = $SigningReport.VerifiedAtUtc
                    requiredFileCount = $SigningReport.RequiredFileCount
                    signedFileCount   = $SigningReport.SignedFileCount
                    allowlistPolicy   = [System.IO.Path]::GetFileName([string]$SigningReport.PolicyPath)
                }
            }
        }
        else {
            $metadata['signing'] = [ordered]@{
                certificateSubject    = $CertificateSubject
                certificateThumbprint = $CertificateThumbprint
                timestampServer       = $TimestampServer
            }
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

function New-ReleasePackageZip {
    <#
        Writes a deterministic ZIP of the whole package folder: entries added in ordinal order
        with a fixed entry timestamp so two builds of the same content produce the same archive.
        Lives in the module so both New-ReleasePackage and Sign-Release rebuild the ZIP the same
        way — the shipped ZIP must always contain the signed binaries, never a pre-signing copy.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$DestinationZip
    )

    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    if (Test-Path -LiteralPath $DestinationZip) { Remove-Item -LiteralPath $DestinationZip -Force }

    $sourceFull = (Resolve-Path -LiteralPath $PackageRoot).Path
    $relatives = @()
    foreach ($file in (Get-ChildItem -LiteralPath $sourceFull -Recurse -File -Force)) {
        $relatives += (Get-ReleaseRelativePath -Root $sourceFull -FullName $file.FullName)
    }
    $relatives = @(Get-OrdinalSortedString -Value $relatives)

    $fixedTime = [System.DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    $stream = [System.IO.File]::Open($DestinationZip, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($relative in $relatives) {
                $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTime
                $sourceFile = Join-Path $sourceFull ($relative -replace '/', '\')
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
    return $DestinationZip
}

function Test-ReleaseZipIntegrity {
    <#
        Final gate on the shipped artifact: extracts the ZIP to a scratch directory and verifies
        the extracted package exactly as a target machine would — checksums, secret exclusion,
        signing status, and (when requested) the full signing report. This catches a ZIP that was
        built before signing or that no longer matches the package folder.
        Always cleans up the scratch directory.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [switch]$RequireSigned,
        [object]$SigningReportParameters
    )

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("tia-zipverify-" + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($scratch) | Out-Null
    try {
        $extracted = Expand-ReleasePackage -ReleaseZipPath $ZipPath -DestinationRoot $scratch
        $integrity = Test-ReleasePackageIntegrity -PackageRoot $extracted -RequireSigned:$RequireSigned
        $messages = @($integrity.Messages)
        $signingOk = $true

        if ($RequireSigned) {
            $splat = @{ PackageRoot = $extracted }
            if ($null -ne $SigningReportParameters) {
                foreach ($property in $SigningReportParameters.GetEnumerator()) { $splat[$property.Key] = $property.Value }
            }
            $report = New-ReleaseSigningReport @splat
            $signingOk = $report.Ok
            $messages += @($report.Messages)
        }

        return [pscustomobject]@{
            Ok            = ($integrity.Ok -and $signingOk)
            IntegrityOk   = $integrity.Ok
            SigningOk     = $signingOk
            SigningStatus = $integrity.SigningStatus
            Messages      = $messages
        }
    }
    finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
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
    Get-ReleaseSigningPolicy, `
    Test-ReleasePathPattern, `
    Resolve-ReleaseSigningPlan, `
    Get-AuthenticodeSignatureBlob, `
    Get-AuthenticodeSignatureDetail, `
    Test-ReleaseSignatureFile, `
    New-ReleaseSigningReport, `
    New-ReleaseSigningOption, `
    Test-SignToolArgumentSafety, `
    Get-SignToolPath, `
    Test-ReleaseZipEntry, `
    New-ReleasePackageZip, `
    Test-ReleaseZipIntegrity, `
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
