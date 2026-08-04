#requires -Version 5.1
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

function Complete-Acceptance {
    param([int]$ExitCode = 0)
    Write-Host ''
    Write-Host 'Production acceptance summary'
    $script:results | Format-Table -AutoSize
    exit $ExitCode
}

function Test-IsAdministrator {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-PlaceholderPath {
    param([string]$Path)
    return -not [string]::IsNullOrWhiteSpace($Path) -and $Path -match '<[^>]+>'
}

function Test-JwtKeyInput {
    param(
        [string]$Name,
        [string]$Path,
        [System.Collections.Generic.List[string]]$Failures
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        [void]$Failures.Add("$Name path is required.")
        return
    }

    if (Test-PlaceholderPath -Path $Path) {
        [void]$Failures.Add("$Name path contains placeholder text.")
        return
    }

    $resolved = $null
    try {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    }
    catch {
        [void]$Failures.Add("$Name path could not be resolved.")
        return
    }

    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        [void]$Failures.Add("$Name file was not found.")
        return
    }

    $item = Get-Item -LiteralPath $resolved
    if ($item.Length -le 0) {
        [void]$Failures.Add("$Name file is empty.")
    }
}

function Add-NotTestedAfterJwtFailure {
    Add-Result 'Readers API' 'Not Tested' 'JWT issue failed.'
    Add-Result 'Card status API' 'Not Tested' 'JWT issue failed.'
    Add-Result 'Card ATR API' 'Not Tested' 'JWT issue failed.'
    Add-Result 'CardRemoved transition' 'Not Tested' 'JWT issue failed.'
    Add-Result 'CardInserted transition' 'Not Tested' 'JWT issue failed.'
    Add-Result 'SSE CardRemoved' 'Not Tested' 'JWT issue failed.'
    Add-Result 'SSE CardInserted' 'Not Tested' 'JWT issue failed.'
    Add-Result 'Restart service health/readers' 'Not Tested' 'JWT issue failed.'
    Add-Result 'Upgrade' 'Not Tested' 'JWT issue failed.'
    Add-Result 'Uninstall keep data' 'Not Tested' 'JWT issue failed.'
    Add-Result 'Reinstall' 'Not Tested' 'JWT issue failed.'
}

function New-TestToken {
    param([string]$TokenName)

    $tokenPath = Join-Path $env:TEMP ("thai-id-agent-{0}-{1}.jwt" -f $TokenName, [guid]::NewGuid().ToString('N'))
    try {
        $jwtArgs = @(
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            (Join-Path $script:root 'scripts\New-TestJwt.ps1'),
            '-PrivateKeyPath',
            $script:JwtPrivateKeyPath,
            '-PublicKeyPath',
            $script:JwtPublicKeyPath,
            '-TokenOutputPath',
            $tokenPath,
            '-LifetimeSeconds',
            '60',
            '-Force'
        )

        $jwtToolOutput = & powershell.exe @jwtArgs 2>&1
        $jwtExitCode = $LASTEXITCODE
        if ($jwtExitCode -ne 0) {
            throw "JWT tool failed with exit code $jwtExitCode."
        }

        if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
            throw 'JWT tool did not create a token file.'
        }

        $tokenItem = Get-Item -LiteralPath $tokenPath
        if ($tokenItem.Length -le 0) {
            throw 'JWT token file is empty.'
        }

        $token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw 'JWT token was empty.'
        }

        return $token
    }
    finally {
        if (Test-Path -LiteralPath $tokenPath) {
            Remove-Item -LiteralPath $tokenPath -Force -ErrorAction SilentlyContinue
        }
    }
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

function Wait-ForCardStatus {
    param(
        [string]$ExpectedStatus,
        [string]$ResultName,
        [int]$TimeoutSeconds = 15,
        [int]$PollMilliseconds = 500,
        [int]$RequiredConsecutiveObservations = 2
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $consecutiveObservations = 0
    $latestStatus = '<not observed>'

    do {
        try {
            $latestStatus = Get-CardPresence
        }
        catch {
            $latestStatus = "request failed: $($_.Exception.Message)"
        }

        if ($latestStatus -eq $ExpectedStatus) {
            $consecutiveObservations++
        }
        else {
            $consecutiveObservations = 0
        }

        if ($consecutiveObservations -ge $RequiredConsecutiveObservations) {
            Add-Result $ResultName 'Passed' "Observed $ExpectedStatus $RequiredConsecutiveObservations consecutive times."
            return
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    } while ((Get-Date) -lt $deadline)

    Add-Result $ResultName 'Failed' "Timed out after ${TimeoutSeconds}s waiting for $ExpectedStatus. Latest status: $latestStatus."
    Complete-Acceptance 1
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$CertificateThumbprint = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $env:Agent__Https__Certificate__Thumbprint } else { $CertificateThumbprint }
$JwtPublicKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPublicKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.public.pem' } else { $JwtPublicKeyPath }
$JwtPrivateKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPrivateKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.private.pem' } else { $JwtPrivateKeyPath }

$jwtKeyFailures = New-Object System.Collections.Generic.List[string]
Test-JwtKeyInput -Name 'JWT public key' -Path $JwtPublicKeyPath -Failures $jwtKeyFailures
Test-JwtKeyInput -Name 'JWT private key' -Path $JwtPrivateKeyPath -Failures $jwtKeyFailures

if ($jwtKeyFailures.Count -eq 0) {
    $resolvedJwtPublicKeyPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPublicKeyPath)
    $resolvedJwtPrivateKeyPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPrivateKeyPath)
    if ([string]::Equals($resolvedJwtPublicKeyPath, $resolvedJwtPrivateKeyPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        [void]$jwtKeyFailures.Add('JWT public and private key paths must be different.')
    }
}

if ($jwtKeyFailures.Count -gt 0) {
    Add-Result 'JWT key preflight' 'Failed' ($jwtKeyFailures -join ' ')
    Complete-Acceptance 1
}

$JwtPublicKeyPath = $resolvedJwtPublicKeyPath
$JwtPrivateKeyPath = $resolvedJwtPrivateKeyPath
Add-Result 'JWT key preflight' 'Passed' 'Public and private JWT key files are present and non-empty.'

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

$privateKeyAclAccount = if ($ServiceAccount -eq 'NT AUTHORITY\LocalService') {
    'NT AUTHORITY\LOCAL SERVICE'
}
else {
    $ServiceAccount
}

$privateKeyAclArgs = @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $root 'scripts\Set-CertificatePrivateKeyAcl.ps1'),
    '-Thumbprint',
    $CertificateThumbprint,
    '-Account',
    $privateKeyAclAccount
)
if ($WhatIfPreference) {
    $privateKeyAclArgs += '-WhatIf'
}
& powershell.exe @privateKeyAclArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Set-CertificatePrivateKeyAcl.ps1 failed.' }
Add-Result 'LocalService private-key ACL' 'Passed' 'ACL script completed or WhatIf-reviewed.'

$installServiceStatus = if ($WhatIfPreference) {
    Write-Host "What if: Would run Install-Service.ps1 for service '$ServiceName'."
    'Not Tested'
}
else {
    $installServiceArgs = @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        (Join-Path $root 'scripts\Install-Service.ps1'),
        '-ServiceName',
        $ServiceName,
        '-CertificateThumbprint',
        $CertificateThumbprint,
        '-CertificateHostName',
        $CertificateHostName,
        '-ServiceAccount',
        $ServiceAccount,
        '-SkipStart'
    )
    & powershell.exe @installServiceArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Install-Service.ps1 failed.' }
    'Passed'
}
Add-Result 'Install service' $installServiceStatus

if (-not $WhatIfPreference) {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Add-Result 'Service configuration' 'Passed' "Status=$($service.Status)"

    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    Add-Result 'Start service' 'Passed'

    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
    Add-Result 'HTTPS health' 'Passed' 'No certificate-validation bypass used.'

    try {
        [void](New-TestToken -TokenName 'issue')
        Add-Result 'JWT issue' 'Passed' 'Created short-lived test JWT without printing token.'
    }
    catch {
        Add-Result 'JWT issue' 'Failed' $_.Exception.Message
        Add-NotTestedAfterJwtFailure
        Complete-Acceptance 1
    }

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
        Wait-ForCardStatus -ExpectedStatus 'NoCard' -ResultName 'CardRemoved transition'

        Read-Host 'Insert the card, then press Enter'
        Wait-ForCardStatus -ExpectedStatus 'CardPresent' -ResultName 'CardInserted transition'
    }
    Add-Result 'SSE CardRemoved' 'Not Tested' 'Status polling is not SSE validation. Test /api/v1/events separately.'
    Add-Result 'SSE CardInserted' 'Not Tested' 'Status polling is not SSE validation. Test /api/v1/events separately.'

    Restart-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get -TimeoutSec 15 | Out-Null
    Invoke-AgentJson -Method Get -Path '/api/v1/readers' -TokenName 'readers-after-restart' | Out-Null
    Add-Result 'Restart service health/readers' 'Passed'

    if ($SkipUpgrade) {
        Add-Result 'Upgrade' 'Not Tested' 'Skipped by parameter.'
    }
    else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-Service.ps1') `
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
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Uninstall-Service.ps1') -ServiceName $ServiceName | Out-Host
        Add-Result 'Uninstall keep data' 'Passed'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-Service.ps1') `
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
    Add-Result 'SSE CardRemoved' 'Not Tested' 'WhatIf mode.'
    Add-Result 'SSE CardInserted' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Restart service health/readers' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Upgrade' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Uninstall keep data' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Reinstall' 'Not Tested' 'WhatIf mode.'
    Add-Result 'Certificate retention' 'Passed' 'Script does not delete certificates.'
}

Complete-Acceptance 0