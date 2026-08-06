#requires -Version 5.1
<#
.SYNOPSIS
    Verifies the integrity and (optionally) the Authenticode signatures of a release package.

.DESCRIPTION
    Always verifies the SHA-256 checksum manifest first (fail closed on tamper). Then reads
    release-manifest.json and inspects Authenticode signatures on the service executable and
    the project's own assemblies.

    -RequireSigned turns an unsigned or partially signed package into a failure. Without it,
    an UnsignedPilot package is reported with a warning and a success exit code so pilot
    verification can proceed.

.NOTES
    Windows PowerShell 5.1 compatible. Non-zero exit on failure so callers can gate on it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [switch]$RequireSigned,
    [switch]$RequireTimestamp
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

# 3. Authenticode status of signable files.
$targets = @(Get-ChildItem -LiteralPath $payloadDir -Recurse -File -Force |
        Where-Object { $_.Extension -ieq '.exe' -or ($_.Extension -ieq '.dll' -and $_.Name -like 'ThaiIdCardAgent.*') })

# A file is "signed" when a signer certificate is attached. 'HashMismatch' means the file was
# modified after signing (tamper) and is always rejected. 'NotSigned'/no signer is unsigned.
# A non-Valid status with a signer present (e.g. the signing CA is not installed on this
# verification machine) counts as signed-but-untrusted: OS publisher trust is a target-machine
# concern, so it is reported with a warning rather than rejected.
$valid = 0
$unsigned = 0
$invalid = 0
$untrusted = 0
$noTimestamp = 0
foreach ($file in $targets) {
    $rel = Get-ReleaseRelativePath -Root $packageRoot -FullName $file.FullName
    $sig = Get-AuthenticodeSignature -LiteralPath $file.FullName
    $hasTimestamp = ($null -ne $sig.TimeStamperCertificate)
    $hasSigner = ($null -ne $sig.SignerCertificate)

    if ($sig.Status -eq 'HashMismatch') {
        $invalid++
        Write-Host ("INVALID  : {0} [HashMismatch] file modified after signing" -f $rel)
    }
    elseif ($sig.Status -eq 'NotSigned' -or -not $hasSigner) {
        $unsigned++
        Write-Host "NotSigned: $rel"
    }
    elseif ($sig.Status -eq 'Valid') {
        $valid++
        if (-not $hasTimestamp) { $noTimestamp++ }
        $tsNote = if ($hasTimestamp) { ' (timestamped)' } else { ' (no timestamp)' }
        Write-Host ("Valid    : {0}{1}" -f $rel, $tsNote)
    }
    else {
        # Signature applied but chain not trusted on this machine.
        $valid++
        $untrusted++
        if (-not $hasTimestamp) { $noTimestamp++ }
        Write-Host ("Signed   : {0} (untrusted chain here: {1})" -f $rel, $sig.Status)
    }
}

Write-Host ''
Write-Host "Targets: $($targets.Count)  Signed: $valid  NotSigned: $unsigned  Invalid: $invalid  UntrustedChain: $untrusted"
if ($untrusted -gt 0) {
    Write-Warning "$untrusted signed file(s) do not chain to a trusted root on this machine. Ensure the signing CA is trusted on target machines."
}

# 4. Gate.
if ($invalid -gt 0) {
    throw "$invalid file(s) have an invalid signature. Package REJECTED."
}

if ($RequireSigned) {
    if ($signingStatus -ne 'Signed') {
        throw "RequireSigned: package signing status is '$signingStatus', not 'Signed'. REJECTED."
    }
    if ($unsigned -gt 0) {
        throw "RequireSigned: $unsigned target file(s) are not signed. REJECTED."
    }
    if ($RequireTimestamp -and $noTimestamp -gt 0) {
        throw "RequireSigned + RequireTimestamp: $noTimestamp signed file(s) have no timestamp. REJECTED."
    }
    Write-Host 'RequireSigned: PASSED.'
    return
}

if ($signingStatus -ne 'Signed' -or $unsigned -gt 0) {
    Write-Warning 'Package is an UNSIGNED PILOT build. Do not use for production distribution.'
}
Write-Host 'Verification PASSED.'
