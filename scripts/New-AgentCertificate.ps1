#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string[]]$DnsName = @('localhost'),
    [string]$CertStoreLocation = 'Cert:\LocalMachine\My',
    [string]$PublicCertificatePath,
    [int]$ValidYears = 2,
    [switch]$TrustForLocalMachine,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator -and -not $WhatIfPreference) {
    throw 'Administrator rights are required to create a LocalMachine certificate or modify LocalMachine trusted roots.'
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$PublicCertificatePath = if ([string]::IsNullOrWhiteSpace($PublicCertificatePath)) { Join-Path $root 'artifacts\localhost-public.cer' } else { $PublicCertificatePath }
$resolvedPublicCertificatePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublicCertificatePath)
$publicCertificateDirectory = Split-Path -Parent $resolvedPublicCertificatePath
if (-not [string]::IsNullOrWhiteSpace($publicCertificateDirectory)) {
    New-Item -ItemType Directory -Force -Path $publicCertificateDirectory | Out-Null
}

if ($DnsName.Count -eq 0 -or [string]::IsNullOrWhiteSpace($DnsName[0])) {
    throw 'At least one DNS SAN must be supplied. The first DNS name is used as the certificate subject.'
}

$primaryDnsName = $DnsName[0]
$existing = Get-ChildItem -LiteralPath $CertStoreLocation -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq "CN=$primaryDnsName" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existing -and -not $Force) {
    Write-Host "Existing certificate found. Thumbprint: $($existing.Thumbprint)"
    Write-Host 'Use -Force only after verifying a new certificate should be created. No existing certificate was deleted.'
    return
}

$certificate = $existing
$target = "$CertStoreLocation CN=$primaryDnsName"
if (-not $certificate -or $Force) {
    if ($PSCmdlet.ShouldProcess($target, 'Create HTTPS server certificate with Server Authentication EKU')) {
        $certificate = New-SelfSignedCertificate `
            -DnsName $DnsName `
            -CertStoreLocation $CertStoreLocation `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -KeyExportPolicy NonExportable `
            -HashAlgorithm SHA256 `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
            -NotAfter (Get-Date).AddYears($ValidYears)

        Write-Host "Created certificate thumbprint: $($certificate.Thumbprint)"
    }
}

if (-not $certificate) {
    Write-Host "WhatIf: certificate would be created for CN=$primaryDnsName."
    return
}

if ($PSCmdlet.ShouldProcess($resolvedPublicCertificatePath, 'Export public certificate only')) {
    Export-Certificate -Cert $certificate -FilePath $resolvedPublicCertificatePath -Force | Out-Null
    Write-Host "Exported public certificate: $resolvedPublicCertificatePath"
}

if ($TrustForLocalMachine) {
    Write-Warning 'You are about to add this public certificate to Cert:\LocalMachine\Root. This changes machine-wide trust. Verify the thumbprint before continuing.'
    Write-Warning "Thumbprint: $($certificate.Thumbprint)"
    if ($PSCmdlet.ShouldProcess('Cert:\LocalMachine\Root', "Trust public certificate $($certificate.Thumbprint) for LocalMachine")) {
        Import-Certificate -FilePath $resolvedPublicCertificatePath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
        Write-Host 'Trusted public certificate in Cert:\LocalMachine\Root.'
    }
}
else {
    Write-Host 'Trusted Root was not modified. Re-run with -TrustForLocalMachine only after explicit approval.'
}
