[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string[]]$DnsName = @('localhost', '127.0.0.1'),
    [string]$CertStoreLocation = 'Cert:\LocalMachine\My',
    [int]$ValidYears = 2,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $WhatIfPreference) {
    throw 'Administrator rights are required.'
}

$primaryDnsName = $DnsName[0]
$existing = Get-ChildItem -LiteralPath $CertStoreLocation -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq "CN=$primaryDnsName" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existing -and -not $Force) {
    Write-Host "Existing certificate found. Thumbprint: $($existing.Thumbprint)"
    Write-Host 'Use -Force only after verifying the existing certificate should be replaced.'
    return
}

$target = "$CertStoreLocation CN=$primaryDnsName"
if ($PSCmdlet.ShouldProcess($target, 'Create development HTTPS certificate')) {
    $certificate = New-SelfSignedCertificate `
        -DnsName $DnsName `
        -CertStoreLocation $CertStoreLocation `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears($ValidYears)

    Write-Host "Created certificate thumbprint: $($certificate.Thumbprint)"
}
