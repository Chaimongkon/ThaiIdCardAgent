# Release Process

This document describes how to build a reproducible, verifiable ThaiIdCardAgent release
package for pilot deployment, how to (optionally) sign it, and how integrity is checked
at install time.

All scripts are Windows PowerShell 5.1 compatible and support `-WhatIf` where they make
changes.

## Overview

```
Publish (win-x64, self-contained single file)
  -> New-ReleasePackage.ps1  -> versioned package folder + SHA-256 manifest + release-manifest.json + zip
  -> Sign-Release.ps1        -> Authenticode signatures (optional for pilot) + refreshed manifest
  -> Test-ReleaseSignature.ps1 -> verify checksum (+ signature when RequireSigned)
  -> Install-Service.ps1     -> verify integrity, rollback-protected copy, install/upgrade
```

Shared, unit-tested logic lives in [`scripts/ReleasePackaging.psm1`](../scripts/ReleasePackaging.psm1)
and is covered by `tests/ThaiIdCardAgent.Release.Tests`.

## Package layout

The package contains **2 top-level metadata files + 2 application files = 4 files total** for the
self-contained single-file Windows Service build:

```
ThaiIdCardAgent-<version>-win-x64/
  checksums.sha256         # (metadata) SHA-256 of every file under app/ (deterministic, ordinal order)
  release-manifest.json    # (metadata) product/version/commit/build time/runtime/signing status/file hashes
  app/                     # application payload (covered by checksums.sha256)
    ThaiIdCardAgent.Service.exe   # self-contained single file (runtime + all managed DLLs bundled)
    appsettings.json
ThaiIdCardAgent-<version>-win-x64.zip
```

The publish also emits debug symbols (`*.pdb`), `appsettings.Development.json`, and IIS/static-asset
artifacts (`web.config`, `aspnetcorev2_inprocess.dll`, `*.staticwebassets.endpoints.json`). These are
**excluded** from the package: the agent self-hosts via Kestrel under `UseWindowsService()` (not IIS)
and serves no static files, so none of them are used at runtime.

- `checksums.sha256` lines are `"<64-hex-uppercase><two spaces><forward-slash relative path>"`,
  sorted with an ordinal comparer, written as UTF-8 (no BOM) with LF endings.
- `release-manifest.json` never contains secrets. Certificate subject/thumbprint appear only
  when the package is `Signed`.

## 1. Create an unsigned pilot package

```powershell
# From the repository root
.\scripts\New-ReleasePackage.ps1 -Version '0.1.0-pilot'
```

What it does:

1. Runs `Publish-WinX64.ps1` (win-x64, self-contained, single file). Use `-SkipPublish
   -PublishPath <dir>` to package an existing publish output.
2. Copies the publish output into `app/`.
3. **Refuses to package** if any forbidden secret file is present (`*.pfx`, `*.key`,
   `*.pem`, `*.jwt`, `.env`/`.env.local`, `appsettings.*.local.json`, `*.log`, etc.).
4. Writes `checksums.sha256` and `release-manifest.json` (`signingStatus = UnsignedPilot`).
5. Produces a deterministic zip (entries added in ordinal order with a fixed timestamp).
6. Re-verifies checksums before finishing.

Output goes to `artifacts/release/` (git-ignored). Preview without side effects using
`-WhatIf`.

## 2. Sign the package (production) or accept unsigned (pilot)

See [CODE-SIGNING.md](CODE-SIGNING.md) for full details.

```powershell
# Production: certificate from the Windows store
.\scripts\Sign-Release.ps1 -PackagePath .\artifacts\release\ThaiIdCardAgent-0.1.0-pilot-win-x64 `
    -CertificateThumbprint <THUMBPRINT> -TimestampServer http://timestamp.digicert.com

# Production: certificate from a PFX (password as SecureString)
$pw = Read-Host -AsSecureString 'PFX password'
.\scripts\Sign-Release.ps1 -PackagePath <package> -PfxPath <path.pfx> -PfxPassword $pw `
    -TimestampServer http://timestamp.digicert.com

# Pilot: explicit unsigned mode (loud warning, stays UnsignedPilot)
.\scripts\Sign-Release.ps1 -PackagePath <package> -Unsigned
```

Signing flips `signingStatus` to `Signed`, records the certificate subject/thumbprint and
timestamp server in `release-manifest.json`, and **refreshes the checksum manifest** because
the signed files changed.

## 3. Verify a package

```powershell
# Integrity only (unsigned pilot passes with a warning)
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package>

# Require production signatures (fails on unsigned/invalid; add -RequireTimestamp to require timestamps)
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> -RequireSigned
```

The verifier checks the checksum manifest first (fail closed on tamper), then the
Authenticode status of the service executable and the project's own assemblies.

## 4. Install / upgrade with integrity enforcement

```powershell
# Verified install from a package (checksum enforced)
.\scripts\Install-Service.ps1 -PackagePath <package>

# Verified install requiring valid signatures
.\scripts\Install-Service.ps1 -PackagePath <package> -RequireSigned

# Legacy path (flat publish output, no manifest) still works unchanged
.\scripts\Install-Service.ps1
```

Install/upgrade:

- Verifies the checksum manifest before copying (`-RequireSigned` additionally requires valid
  signatures).
- Backs up existing config, keeps config/logs in `ProgramData` untouched during upgrade.
- Uses a **rollback-protected copy**: the previous binaries are snapshotted and restored if
  the copy fails, so a partial copy never replaces a working install.
- Supports `-WhatIf`.

## Reproducibility

- Manifest and zip entry ordering are ordinal (locale-independent).
- Zip entries use a fixed timestamp.
- `release-manifest.json` records the git commit (with a `-dirty` suffix when the working
  tree is not clean) and the UTC build time so any package can be traced back to source.

## What is never shipped or committed

- Private keys, PFX/P12, `*.key`, `*.pem`, JWTs, `.env`/`.env.local`, certificate passwords,
  logs, or personal card data.
- Generated release output (`artifacts/release/`, `*.zip`, `release-manifest.json`,
  `checksums.sha256`) is git-ignored.

## Clean-machine acceptance

Once a package/ZIP is built, verify and deploy it on a clean machine from the ZIP alone with
[`scripts/Test-PilotDeployment.ps1`](../scripts/Test-PilotDeployment.ps1) (modes: VerifyOnly,
Tamper, Rollback, Full). Read-only sanitized diagnostics come from
[`scripts/Get-AgentDiagnostics.ps1`](../scripts/Get-AgentDiagnostics.ps1). See
[PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md).

## Related

- [CODE-SIGNING.md](CODE-SIGNING.md) — certificate requirements, timestamping, key rotation,
  compromise response.
- [INSTALLATION.md](INSTALLATION.md) — full install/upgrade/uninstall guide.
- [PILOT-DEPLOYMENT.md](PILOT-DEPLOYMENT.md) — multi-machine pilot rollout.
- [PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md) — clean-machine acceptance checklist.
