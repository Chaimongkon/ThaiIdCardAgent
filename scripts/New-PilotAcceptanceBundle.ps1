#requires -Version 5.1
<#
.SYNOPSIS
    Builds a self-contained Clean-Machine Acceptance Bundle for ThaiIdCardAgent.

.DESCRIPTION
    Creates a standalone delivery bundle that contains all necessary scripts, pre-published
    self-contained TestJwt CLI tool, release ZIP packages (0.1.0-pilot and 0.1.1-pilot),
    pilot instructions README, and a strict TOOLING-SHA256.txt checksum manifest.

    The bundle contains NO source code, NO private keys, NO JWT tokens, NO PFX files,
    NO certificate passwords, NO environment files (.env.local), NO logs, and NO PII.
    The clean machine does not require .NET SDK, Visual Studio, Git, NuGet, or the repository.

.NOTES
    Windows PowerShell 5.1 compatible.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$OutputPath,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version010ZipPath,
    [string]$Version011ZipPath,
    [switch]$SkipPublishTestJwt,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$rootCandidate = (Join-Path $scriptDir '..')
$root = if (Test-Path -LiteralPath (Join-Path $rootCandidate 'artifacts')) { (Resolve-Path -LiteralPath $rootCandidate).Path } else { $scriptDir }

Import-Module (Join-Path $scriptDir 'ReleasePackaging.psm1') -Force -DisableNameChecking

$OutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $root 'artifacts\pilot-acceptance-bundle'
} else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
}

$Version010ZipPath = if ([string]::IsNullOrWhiteSpace($Version010ZipPath)) {
    Join-Path $root 'artifacts\release\ThaiIdCardAgent-0.1.0-pilot-win-x64.zip'
} else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Version010ZipPath)
}

$Version011ZipPath = if ([string]::IsNullOrWhiteSpace($Version011ZipPath)) {
    Join-Path $root 'artifacts\release\ThaiIdCardAgent-0.1.1-pilot-win-x64.zip'
} else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Version011ZipPath)
}

if (-not (Test-Path -LiteralPath $Version010ZipPath -PathType Leaf)) {
    throw "Version 0.1.0 release ZIP was not found: $Version010ZipPath"
}
if (-not (Test-Path -LiteralPath $Version011ZipPath -PathType Leaf)) {
    throw "Version 0.1.1 release ZIP was not found: $Version011ZipPath"
}

if ($PSCmdlet.ShouldProcess($OutputPath, 'Create clean-machine pilot acceptance bundle')) {
    if (Test-Path -LiteralPath $OutputPath) {
        if (-not $Force) {
            Write-Host "Cleaning existing bundle directory: $OutputPath"
        }
        Remove-Item -LiteralPath $OutputPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
    $packagesDir = Join-Path $OutputPath 'packages'
    New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null

    # 1. Copy release ZIPs
    Copy-Item -LiteralPath $Version010ZipPath -Destination (Join-Path $packagesDir 'ThaiIdCardAgent-0.1.0-pilot-win-x64.zip') -Force
    Copy-Item -LiteralPath $Version011ZipPath -Destination (Join-Path $packagesDir 'ThaiIdCardAgent-0.1.1-pilot-win-x64.zip') -Force

    # 2. Copy standalone scripts
    $requiredScripts = @(
        'Test-PilotDeployment.ps1',
        'ReleasePackaging.psm1',
        'Install-Service.ps1',
        'Uninstall-Service.ps1',
        'Set-CertificatePrivateKeyAcl.ps1',
        'Test-ReleaseSignature.ps1',
        'Test-SseEvents.ps1',
        'Get-AgentDiagnostics.ps1'
    )

    foreach ($scriptName in $requiredScripts) {
        $sourceScript = Join-Path $scriptDir $scriptName
        if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
            throw "Required script was not found: $sourceScript"
        }
        Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $OutputPath $scriptName) -Force
    }

    # 3. Publish self-contained ThaiIdCardAgent.TestJwt.exe (win-x64 single file)
    $toolProj = Join-Path $root 'tools\ThaiIdCardAgent.TestJwt\ThaiIdCardAgent.TestJwt.csproj'
    $toolExe = Join-Path $OutputPath 'ThaiIdCardAgent.TestJwt.exe'

    if (-not $SkipPublishTestJwt) {
        if (-not (Test-Path -LiteralPath $toolProj -PathType Leaf)) {
            throw "TestJwt project was not found: $toolProj"
        }

        $publishTemp = Join-Path $env:TEMP ("tia-bundle-jwt-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null
        try {
            Write-Host "Publishing self-contained ThaiIdCardAgent.TestJwt for $Runtime..."
            $publishArgs = @(
                'publish', $toolProj,
                '-c', $Configuration,
                '-m:1', '/nr:false',
                '-r', $Runtime,
                '--self-contained', 'true',
                '-p:PublishSingleFile=true',
                '-p:PublishTrimmed=false',
                '-o', $publishTemp
            )
            & dotnet @publishArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Publishing ThaiIdCardAgent.TestJwt failed (exit $LASTEXITCODE)."
            }

            $publishedExe = Join-Path $publishTemp 'ThaiIdCardAgent.TestJwt.exe'
            if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
                throw "Published executable was not found: $publishedExe"
            }

            Copy-Item -LiteralPath $publishedExe -Destination $toolExe -Force
        }
        finally {
            if (Test-Path -LiteralPath $publishTemp) {
                try { [System.IO.Directory]::Delete($publishTemp, $true) } catch { }
            }
        }
    }
    else {
        Write-Warning 'Skipped publishing TestJwt (test flag used).'
    }

    # 4. Generate README.md for the pilot machine
    $readmeContent = @"
# ThaiIdCardAgent Pilot Machine Acceptance Bundle

This bundle contains all artifacts and automated scripts required to verify and accept
the ThaiIdCardAgent pilot deployment on a clean Windows machine.

> **Security & Authenticity Notice:**
> - SHA-256 checksums in ``TOOLING-SHA256.txt`` provide **integrity** verification (tamper detection)
>   for this bundle, but do **not** confer publisher authenticity or identity trust.
> - Formal production deployments require Authenticode Code Signing certificates.
> - Packages built from a dirty working tree (with ``-dirty`` in metadata) are strictly for testing.
>   For production/formal release, packages must be built from a clean, tagged git commit.

---

## Prerequisites (on the Pilot Machine)

1. Windows 10/11 x64 or Windows Server 2019/2022.
2. Windows PowerShell 5.1 (Run as Administrator for installation and service management).
3. HTTPS certificate present in ``LocalMachine\My`` (e.g. ``localhost`` self-signed or CA-issued).
4. Acceptance JWT public key PEM and private key PEM (stored outside this bundle; passed via parameters).
5. **No .NET SDK, Visual Studio, Git, or source tree is required.**

---

## 1. Verify Tooling Bundle Integrity

Before running any scripts, verify the SHA-256 checksum of all bundle files:

````powershell
Import-Module .\ReleasePackaging.psm1
`$verify = Test-ToolingChecksumManifest -BundleRoot .
if (`$verify.Ok) {
    Write-Host "Bundle integrity verification: PASSED" -ForegroundColor Green
} else {
    Write-Error "Bundle integrity verification: FAILED"
    `$verify.Messages
}
````

---

## 2. Acceptance Execution Modes

### A. Verify Only (No install, validates 0.1.0 package integrity)
````powershell
.\Test-PilotDeployment.ps1 -ReleaseZipPath .\packages\ThaiIdCardAgent-0.1.0-pilot-win-x64.zip -Mode VerifyOnly
````

### B. Tamper Detection (Tests rejection of corrupted packages)
````powershell
.\Test-PilotDeployment.ps1 -ReleaseZipPath .\packages\ThaiIdCardAgent-0.1.0-pilot-win-x64.zip -Mode Tamper
````

### C. Rollback Simulation (Tests atomic payload swap & retention on failure)
````powershell
.\Test-PilotDeployment.ps1 -ReleaseZipPath .\packages\ThaiIdCardAgent-0.1.0-pilot-win-x64.zip -Mode Rollback
````

### D. Full Acceptance (Administrator)
````powershell
.\Test-PilotDeployment.ps1 -ReleaseZipPath .\packages\ThaiIdCardAgent-0.1.0-pilot-win-x64.zip -Mode Full `
    -UpgradeZipPath .\packages\ThaiIdCardAgent-0.1.1-pilot-win-x64.zip `
    -CertificateThumbprint "<CERT_THUMBPRINT>" `
    -CertificateHostName "localhost" `
    -JwtPublicKeyPath "C:\AcceptanceKeys\signing.public.pem" `
    -JwtPrivateKeyPath "C:\AcceptanceKeys\signing.private.pem" `
    -AllowedOrigin "https://localhost:3000"
````

### E. Post-Reboot Verification (After Windows reboot)
````powershell
.\Test-PilotDeployment.ps1 -ReleaseZipPath .\packages\ThaiIdCardAgent-0.1.1-pilot-win-x64.zip -Mode PostReboot `
    -JwtPublicKeyPath "C:\AcceptanceKeys\signing.public.pem" `
    -JwtPrivateKeyPath "C:\AcceptanceKeys\signing.private.pem"
````

### F. Read-Only Diagnostics
````powershell
.\Get-AgentDiagnostics.ps1
.\Get-AgentDiagnostics.ps1 -AsJson > agent-diagnostics.json
````
"@

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $readmePath = Join-Path $OutputPath 'README.md'
    [System.IO.File]::WriteAllText($readmePath, $readmeContent, $utf8NoBom)

    # 5. Strict Secret Exclusion Scan on the entire bundle
    Write-Host "Scanning bundle for forbidden secrets / PII..."
    $secretViolations = @(Test-ReleaseSecretExclusion -Path $OutputPath)
    if ($secretViolations.Count -gt 0) {
        Remove-Item -LiteralPath $OutputPath -Recurse -Force
        throw "Bundle creation aborted: forbidden secret/PII files detected: $($secretViolations -join ', ')"
    }

    # 6. Generate TOOLING-SHA256.txt manifest
    Write-Host "Generating TOOLING-SHA256.txt manifest..."
    $manifestPath = New-ToolingChecksumManifest -BundleRoot $OutputPath

    # 7. Validate the newly created manifest immediately
    $testManifest = Test-ToolingChecksumManifest -BundleRoot $OutputPath
    if (-not $testManifest.Ok) {
        throw "Newly generated tooling manifest failed verification: $($testManifest.Messages -join ', ')"
    }

    Write-Host "Clean-machine pilot acceptance bundle successfully created at: $OutputPath" -ForegroundColor Green
    return $OutputPath
}
