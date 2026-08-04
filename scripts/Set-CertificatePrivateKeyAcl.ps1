#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Thumbprint,
    [string]$Account = 'NT AUTHORITY\LOCAL SERVICE',
    [string]$StoreName = 'My',
    [string]$StoreLocation = 'LocalMachine',
    [switch]$RemoveBroadReadGroups
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $WhatIfPreference) {
    throw 'Administrator rights are required to update certificate private key ACLs.'
}

$normalizedThumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $StoreLocation)
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
try {
    $cert = $store.Certificates |
        Where-Object { ($_.Thumbprint -replace '\s', '').ToUpperInvariant() -eq $normalizedThumbprint } |
        Select-Object -First 1
    if (-not $cert) {
        throw "Certificate was not found in $StoreLocation\$StoreName for thumbprint $normalizedThumbprint."
    }

    $candidatePaths = New-Object System.Collections.Generic.List[string]
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($rsa -is [System.Security.Cryptography.RSACng]) {
        $uniqueName = $rsa.Key.UniqueName
        if ($uniqueName) {
            $candidatePaths.Add((Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$uniqueName"))
        }
    }

    if ($cert.PrivateKey -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
        $uniqueName = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
        if ($uniqueName) {
            $candidatePaths.Add((Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$uniqueName"))
        }
    }

    $keyPath = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $keyPath) {
        throw 'Private key file path could not be resolved for this certificate provider.'
    }

    $aclChanged = $false
    if ($PSCmdlet.ShouldProcess($keyPath, "Grant read access to $Account")) {
        $acl = Get-Acl -LiteralPath $keyPath
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $Account,
            [System.Security.AccessControl.FileSystemRights]::Read,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.SetAccessRule($rule)

        if ($RemoveBroadReadGroups) {
            foreach ($identity in @('Everyone', 'BUILTIN\Users')) {
                $acl.Access |
                    Where-Object { $_.IdentityReference.Value -eq $identity -and $_.FileSystemRights.ToString().Contains('Read') } |
                    ForEach-Object { [void]$acl.RemoveAccessRule($_) }
            }
        }

        Set-Acl -LiteralPath $keyPath -AclObject $acl
        $aclChanged = $true
    }

    Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
    Write-Host "Private key path: $keyPath"
    if ($aclChanged) {
        Write-Host "Granted account: $Account"
    }
    else {
        Write-Host "Target account: $Account"
    }
}
finally {
    $store.Dispose()
}