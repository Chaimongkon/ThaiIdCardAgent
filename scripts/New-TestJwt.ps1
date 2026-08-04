#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PrivateKeyPath,
    [string]$PublicKeyPath,
    [string]$TokenOutputPath,
    [string]$Issuer = 'thai-id-card-agent-client',
    [string]$Audience = 'thai-id-card-agent',
    [string]$Subject = 'operator-1',
    [string]$WorkstationId = $env:COMPUTERNAME,
    [int]$LifetimeSeconds = 60,
    [int]$NotBeforeOffsetSeconds = 0,
    [switch]$OmitWorkstationId,
    [switch]$AllowInvalidLifetime,
    [switch]$GenerateKeyPair,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$PrivateKeyPath = if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.private.pem' } else { $PrivateKeyPath }
$PublicKeyPath = if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.public.pem' } else { $PublicKeyPath }
$TokenOutputPath = if ([string]::IsNullOrWhiteSpace($TokenOutputPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-token.jwt' } else { $TokenOutputPath }

if ($LifetimeSeconds -lt 1 -or (($LifetimeSeconds -gt 60) -and -not $AllowInvalidLifetime)) {
    throw 'LifetimeSeconds must be between 1 and 60 unless -AllowInvalidLifetime is used for negative tests.'
}

$toolProject = Join-Path $root 'tools\ThaiIdCardAgent.TestJwt\ThaiIdCardAgent.TestJwt.csproj'
$resolvedPrivate = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PrivateKeyPath)
$resolvedPublic = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublicKeyPath)
$resolvedToken = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TokenOutputPath)

$args = @(
    'run', '--project', $toolProject, '--configuration', 'Release', '--',
    '--private-key', $resolvedPrivate,
    '--public-key', $resolvedPublic,
    '--token-output', $resolvedToken,
    '--issuer', $Issuer,
    '--audience', $Audience,
    '--subject', $Subject,
    '--workstation-id', $WorkstationId,
    '--lifetime-seconds', [string]$LifetimeSeconds,
    '--not-before-offset-seconds', [string]$NotBeforeOffsetSeconds
)
if ($OmitWorkstationId) { $args += '--omit-workstation-id' }
if ($GenerateKeyPair) { $args += '--generate-key-pair' }
if ($Force) { $args += '--force' }

if ($PSCmdlet.ShouldProcess($resolvedToken, 'Create short-lived test JWT')) {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
