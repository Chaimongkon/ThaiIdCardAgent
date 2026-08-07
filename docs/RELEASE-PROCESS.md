# Release Process

This document describes how to build a reproducible, verifiable ThaiIdCardAgent release
package for pilot deployment, how to (optionally) sign it, and how integrity is checked
at install time.

All scripts are Windows PowerShell 5.1 compatible and support `-WhatIf` where they make
changes.

## Overview

The release stages run in a mandatory order. Nothing is signed until the payload is validated, and
no checksum, manifest, or ZIP is produced until every signature has been verified:

```
1. Publish                        win-x64, self-contained single file
2. Assemble + validate payload    exclusions, secret scan, signing allowlist
3. Sign binaries/scripts          SHA-256 + RFC 3161 timestamp
4. Verify signatures + timestamps every required file, signer, digest, timestamp
5. Generate checksums             SHA-256 manifest over the SIGNED payload
6. Generate manifest              release-manifest.json + signing evidence
7. Create ZIP                     deterministic, from the SIGNED package folder
8. Verify final package           extract the ZIP and verify it as a target machine would
   -> Install-Service.ps1         verify integrity, rollback-protected copy, install/upgrade
```

[`scripts/Invoke-ReleaseBuild.ps1`](../scripts/Invoke-ReleaseBuild.ps1) runs all eight stages in
order and is the entry point for a production release. Underneath: `New-ReleasePackage.ps1` does
stages 1–2 (with `-SkipZip`, so an unsigned ZIP never exists for a signed release) and
`Sign-Release.ps1` does stages 3–8.

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

## 1. Build a release

```powershell
# From the repository root.

# Production (signed) — see RELEASE-SIGNING-WORKFLOW.md
.\scripts\Invoke-ReleaseBuild.ps1 -Version '1.0.0' -SigningConfigPath <config>

# Unsigned pilot
.\scripts\Invoke-ReleaseBuild.ps1 -Version '0.1.0-pilot' -Unsigned
```

What the packaging stage does:

1. Runs `Publish-WinX64.ps1` (win-x64, self-contained, single file). Use `-SkipPublish
   -PublishPath <dir>` to package an existing publish output.
2. Copies the publish output into `app/` and drops non-runtime files.
3. **Refuses to package** if any forbidden secret file is present (`*.pfx`, `*.key`,
   `*.pem`, `*.jwt`, `.env`/`.env.local`, `appsettings.*.local.json`, `*.log`, etc.).
4. **Refuses to package** executable content the signing allowlist does not account for, or a
   payload missing a file the allowlist marks required.
5. Writes `checksums.sha256` and `release-manifest.json`.
6. Produces a deterministic zip (entries added in ordinal order with a fixed timestamp) — deferred
   until after signing for a signed release.
7. Re-verifies checksums, and verifies the ZIP by extracting it.

Output goes to `artifacts/release/` (git-ignored). Preview without side effects using `-WhatIf`.

## 2. Sign the package (production) or accept unsigned (pilot)

`Invoke-ReleaseBuild.ps1` already does this. To run the stage on its own — for example to re-sign an
existing package — see [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) and
[CODE-SIGNING.md](CODE-SIGNING.md).

```powershell
# Production: hardware token / HSM certificate, from the signing configuration.
.\scripts\Sign-Release.ps1 -PackagePath .\artifacts\release\ThaiIdCardAgent-1.0.0-win-x64 `
    -SigningConfigPath <config>

# Development: self-signed certificate from the store.
.\scripts\Sign-Release.ps1 -PackagePath <package> -CertificateThumbprint <THUMBPRINT> -StoreLocation CurrentUser

# Pilot: explicit unsigned mode (loud warning, stays UnsignedPilot)
.\scripts\Sign-Release.ps1 -PackagePath <package> -Unsigned
```

Signing flips `signingStatus` to `Signed` only after every required signature verified. It records
the signer subject/issuer, thumbprint, signature algorithm, timestamp state, certificate validity,
and verification result in `release-manifest.json`, **refreshes the checksum manifest** because the
signed files changed, and **rebuilds the ZIP** from the signed payload.

The timestamp service URL is never a literal in this repository: it is a configuration value that
stays a `<PLACEHOLDER>` until procurement confirms the actual RFC 3161 service.

## 3. Verify a package

```powershell
# Integrity only (unsigned pilot passes with a warning)
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package>

# Require production signatures
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> `
    -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain `
    -ExpectedSignerThumbprint <THUMBPRINT>
```

The verifier checks the checksum manifest first (fail closed on tamper), then the signing allowlist
(no missing required file, no unexpected executable content), then the embedded Authenticode
signature of every allowlisted file.

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

- [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) — the production signing procedure.
- [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md) — production signing workstream.
- [CODE-SIGNING.md](CODE-SIGNING.md) — certificate requirements, timestamping, key rotation,
  compromise response.
- [INSTALLATION.md](INSTALLATION.md) — full install/upgrade/uninstall guide.
- [PILOT-DEPLOYMENT.md](PILOT-DEPLOYMENT.md) — multi-machine pilot rollout.
- [PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md) — clean-machine acceptance checklist.
