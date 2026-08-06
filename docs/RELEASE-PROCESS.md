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

```
ThaiIdCardAgent-<version>-win-x64/
  app/                     # published binaries (payload)
    ThaiIdCardAgent.Service.exe
    ...
  checksums.sha256         # SHA-256 of every file under app/ (deterministic, ordinal order)
  release-manifest.json    # product/version/commit/build time/runtime/signing status/file hashes
ThaiIdCardAgent-<version>-win-x64.zip
```

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

## Related

- [CODE-SIGNING.md](CODE-SIGNING.md) — certificate requirements, timestamping, key rotation,
  compromise response.
- [INSTALLATION.md](INSTALLATION.md) — full install/upgrade/uninstall guide.
- [PILOT-DEPLOYMENT.md](PILOT-DEPLOYMENT.md) — multi-machine pilot rollout.
