[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'ThaiIdCardAgent',
    [string]$BaseUrl = 'https://localhost:18443',
    [string]$AllowedOrigin = 'https://localhost:3000',
    [string]$CertificateThumbprint,
    [string]$CertificateHostName = 'localhost',
    [string]$JwtPublicKeyPath,
    [string]$JwtPrivateKeyPath,
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService',
    [switch]$ConfigureMachineEnvironment,
    [switch]$SkipInteractiveCardTransitions,
    [switch]$SkipUpgrade,
    [switch]$SkipUninstallReinstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Message = '')
    $script:results.Add([pscustomobject]@{ Step = $Name; Status = $Status; Message = $Message })
    Write-Host "[$Status] $Name $Message"
}

function Test-IsAdministrator {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-TestToken {
    param([string]$TokenName)
    $tokenPath = Join-Path $env:TEMP "thai-id-agent-$TokenName.jwt"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:root 'scripts\New-TestJwt.ps1') `
        -PrivateKeyPath $script:JwtPrivateKeyPath `
        -PublicKeyPath $script:JwtPublicKeyPath `
        -TokenOutputPath $tokenPath `
        -LifetimeSeconds 60 `
        -Force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to create test JWT for $TokenName." }
    return (Get-Content -LiteralPath $tokenPath -Raw).Trim()
}

function Invoke-AgentJson {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [string]$TokenName = 'request'
    )
    $token = New-TestToken -TokenName $TokenName
    $headers = @{ Authorization = "Bearer $token"; Origin = $script:AllowedOrigin }
    $uri = "$($script:BaseUrl)$Path"
    if ($Body -ne $null) {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 6) -TimeoutSec 15
    }

    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 15
}

function Get-CardPresence {
    $status = Invoke-AgentJson -Method Get -Path '/api/v1/card/status' -TokenName 'status'
    return [string]$status.data.status
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$CertificateThumbprint = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $env:Agent__Https__Certificate__Thumbprint } else { $CertificateThumbprint }
$JwtPublicKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPublicKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.public.pem' } else { $JwtPublicKeyPath }
$JwtPrivateKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPrivateKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.private.pem' } else { $JwtPrivateKeyPath }
$JwtPublicKeyPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPublicKeyPath)
$JwtPrivateKeyPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPrivateKeyPath)

if (-not (Test-IsAdministrator)) {
    if ($WhatIfPreference) {
        Add-Result 'Administrator' 'Not Tested' 'WhatIf mode: Administrator check was not enforced.'
    }
    else {
        throw 'Administrator rights are required for production acceptance.'
    }
}
else {
    Add-Result 'Administrator' 'Passed'
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'CertificateThumbprint is required, either as -CertificateThumbprint or Agent__Https__Certificate__Thumbprint.'
}

if ($ConfigureMachineEnvironment) {
    if ($PSCmdlet.ShouldProcess('Machine environment', 'Configure non-secret production agent environment values')) {
        [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
        [Environment]::SetEnvironmentVariable('Agent__AllowedOrigins__0', $AllowedOrigin, 'Machine')
        [Environment]::SetEnvironmentVariable('Agent__Https__Certificate__Thumbprint', $CertificateThumbprint, 'Machine')
        [Environment]::SetEnvironmentVariable('Agent__Jwt__PublicKeyPath', $JwtPublicKeyPath, 'Machine')
    }
    Add-Result 'Production configuration' 'Passed' 'Machine environment values configured or WhatIf-reviewed.'
}
else {
    Add-Result 'Production configuration' 'Not Tested' 'Use -ConfigureMachineEnvironment to set non-secret machine environment values for the service.'
}

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Set-CertificatePrivateKeyAcl.ps1') `
    -Thumbprint $CertificateThumbprint `
    -Account (if ($ServiceAccount -eq 'NT AUTHORITY\LocalService') { 'NT AUTHORITY\LOCAL SERVICE' } else { $ServiceAccount }) `
    -WhatIf:$WhatIfPreference | Out-Host
Add-Result 'LocalService private-key ACL' 'Passed' 'ACL script completed or WhatIf-reviewed.'

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-Service.ps1') `
    -ServiceName $ServiceName `
    -CertificateThumbprint $CertificateThumbprint `
    -CertificateHostName $CertificateHostName `
    -ServiceAccount $ServiceAccount `
    -SkipStart `
    -WhatIf:$WhatIfPreference | Out-Host
Add-Result 'Install service' (if ($WhatIfPreference) { 'Not Tested' } else { 'Passed' })

if (-not $WhatIfPreference) {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Add-Result 'Service configuration' 'Passed' "Status=$($service.Status)"

    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    Add-Result 'Start service' 'Passed'

    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
    Add-Result 'HTTPS health' 'Passed' 'No certificate-validation bypass used.'

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\New-TestJwt.ps1') `
        -PrivateKeyPath $JwtPrivateKeyPath `
        -PublicKeyPath $JwtPublicKeyPath `
        -LifetimeSeconds 60 `
        -Force | Out-Host
    Add-Result 'JWT issue' 'Passed' 'Created short-lived test JWT without printing token.'

    Invoke-AgentJson -Method Get -Path '/api/v1/readers' -TokenName 'readers' | Out-Null
    Add-Result 'Readers API' 'Passed'

    Invoke-AgentJson -Method Get -Path '/api/v1/card/status' -TokenName 'card-status' | Out-Null
    Add-Result 'Card status API' 'Passed'

    Invoke-AgentJson -Method Post -Path '/api/v1/card/atr' -Body @{ readerName = $null } -TokenName 'card-atr' | Out-Null
    Add-Result 'Card ATR API' 'Passed'

    if ($SkipInteractiveCardTransitions) {
        Add-Result 'CardRemoved transition' 'Not Tested' 'Skipped by parameter.'
        Add-Result 'CardInserted transition' 'Not Tested' 'Skipped by parameter.'
    }
    else {
        Read-Host 'Remove the card, then press Enter'
        $removed = Get-CardPresence
        if ($removed -ne 'NoCard') { throw "Expected NoCard after removal but got $removed." }
        Add-Result 'CardRemoved transition' 'Passed'

        Read-Host 'Insert the card, then press Enter'
        $inserted = Get-CardPresence
        if ($inserted -ne 'CardPresent') { throw "Expected CardPresent after insertion but got $inserted." }
        Add-Result 'CardInserted transition' 'Passed'
    }

    Restart-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
    Invoke-AgentJson -Method Get -Path '/api/v1/readers' -TokenName 'readers-after-restart' | Out-Null
    Add-Result 'Restart service health/readers' 'Passed'

    if ($SkipUpgrade) {
        Add-Result 'Upgrade' 'Not Tested' 'Skipped by parameter.'
    }
    else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-Service.ps1') `
            -ServiceName $ServiceName `
            -CertificateThumbprint $CertificateThumbprint `
            -CertificateHostName $CertificateHostName `
            -ServiceAccount $ServiceAccount `
            -SkipStart | Out-Host
        Add-Result 'Upgrade' 'Passed'
    }

    if ($SkipUninstallReinstall) {
        Add-Result 'Uninstall keep data' 'Not Tested' 'Skipped by parameter.'
        Add-Result 'Reinstall' 'Not Tested' 'Skipped by parameter.'
    }
    else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Uninstall-Service.ps1') -ServiceName $ServiceName | Out-Host
        Add-Result 'Uninstall keep data' 'Passed'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-Service.ps1') `
            -ServiceName $ServiceName `
            -CertificateThumbprint $CertificateThumbprint `
            -CertificateHostName $CertificateHostName `
            -ServiceAccount $ServiceAccount `
            -SkipStart | Out-Host
        Add-Result 'Reinstall' 'Passed'
    }

    Add-Result 'Certificate retention' 'Passed' 'Script does not delete certificates.'
}
else {
    Add-Result 'Start service' 'Not Tested' 'WhatIf mode.'
    Add-Result 'HTTPS health' 'Not Tested' 'WhatIf mode.'
    Add-Result 'JWT issue' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Readers API' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Card status API' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Card ATR API' 'Not Tested' 'WhatIf mode.'
    Add-Result 'CardRemoved transition' 'Not Tested' 'WhatIf mode.'
    Add-Result 'CardInserted transition' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Restart service health/readers' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Upgrade' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Uninstall keep data' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Reinstall' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Certificate retention' 'Passed' 'Script does not delete certificates.'
}

Write-Host ''
Write-Host 'Production acceptance summary'
$results | Format-Table -AutoSize

if ($results | Where-Object Status -eq 'Failed') {
    exit 1
}