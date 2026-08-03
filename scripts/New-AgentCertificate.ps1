Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

param(
    [string]$DnsName = 'localhost',
    [string]$CertStoreLocation = 'Cert:\LocalMachine\My'
)

$certificate = New-SelfSignedCertificate -DnsName $DnsName -CertStoreLocation $CertStoreLocation -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
Write-Host "Created certificate thumbprint: $($certificate.Thumbprint)"
