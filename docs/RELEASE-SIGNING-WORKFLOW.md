# Release Signing Workflow

The operational procedure for producing a signed ThaiIdCardAgent release. Read
[SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) before performing a signing run —
the rules there are binding.

## Mandatory stage order

```
1. Publish                       win-x64, self-contained single file
2. Assemble + validate payload   exclusions, secret scan, signing allowlist
3. Sign binaries/scripts         signtool, SHA-256, RFC 3161 timestamp
4. Verify signatures + timestamps every required file, signer, digest, timestamp
5. Generate checksums            SHA-256 manifest over the SIGNED payload
6. Generate manifest             release-manifest.json + signing evidence
7. Create ZIP                    deterministic, from the SIGNED package folder
8. Verify final package          extract the ZIP and verify it as a target machine would
```

The order matters in both directions: nothing is signed until the payload has been validated, and
no checksum, manifest, or ZIP is produced until every signature has been verified. A ZIP built
before signing would ship unsigned binaries, so a signed build never creates one —
`New-ReleasePackage.ps1 -SkipZip` defers it and `Sign-Release.ps1` produces the ZIP at stage 7.

[`scripts/Invoke-ReleaseBuild.ps1`](../scripts/Invoke-ReleaseBuild.ps1) runs all eight stages in
order and is the only supported entry point for a production release.

## Prerequisites

| # | Item |
| --- | --- |
| 1 | Designated signing workstation (see custody policy section 3) |
| 2 | Windows SDK signing tools installed (`signtool.exe`) |
| 3 | Token drivers / HSM client installed; certificate visible in `Cert:\LocalMachine\My` with its private key on the token/HSM |
| 4 | Signing configuration file, filled in from `scripts/signing-config.template.json`, stored **outside** the repository with a restricted ACL |
| 5 | Release approved by a Release Approver who is not the Signing Officer |
| 6 | Clean working tree at the release commit (a dirty tree is recorded as `-dirty` in the manifest) |

## Signing configuration

Copy the template out of the repository and fill in the confirmed values:

```powershell
Copy-Item .\scripts\signing-config.template.json C:\Signing\thaiidcardagent-signing.json
notepad C:\Signing\thaiidcardagent-signing.json
```

Replace every `<PLACEHOLDER>`. The tooling refuses to sign while any required placeholder is
unresolved, so the template can never be used as-is.

**The configuration must not contain a PIN, password, or any other credential.**
`New-ReleaseSigningOption` rejects the file outright if it holds a key whose name looks like a
secret (`password`, `pin`, `secret`, `token`, `credential`, `apikey`, …).

## Producing a signed release

```powershell
# From the repository root, at the release commit.
.\scripts\Invoke-ReleaseBuild.ps1 -Version '1.0.0' `
    -SigningConfigPath C:\Signing\thaiidcardagent-signing.json -WhatIf   # dry run first

.\scripts\Invoke-ReleaseBuild.ps1 -Version '1.0.0' `
    -SigningConfigPath C:\Signing\thaiidcardagent-signing.json
```

During stage 3, `signtool` raises the token or HSM PIN prompt. **Enter the PIN interactively.** It
never passes through these scripts and must never be supplied as an argument.

Output:

```
artifacts\release\ThaiIdCardAgent-1.0.0-win-x64\        signed package folder
artifacts\release\ThaiIdCardAgent-1.0.0-win-x64.zip     the distributable, built from the signed payload
```

## Producing an unsigned pilot release

For controlled pilot machines only. Never distribute publicly.

```powershell
.\scripts\Invoke-ReleaseBuild.ps1 -Version '1.0.0-pilot' -Unsigned
```

The package stays `UnsignedPilot`; SmartScreen and "unknown publisher" warnings apply.

## Running the stages individually

Useful for re-signing an existing package or for development. The order is still enforced by the
scripts themselves.

```powershell
# Stages 1-2 only (no ZIP: it must not exist before signing).
.\scripts\New-ReleasePackage.ps1 -Version '1.0.0' -SkipZip

# Stages 3-8.
.\scripts\Sign-Release.ps1 -PackagePath .\artifacts\release\ThaiIdCardAgent-1.0.0-win-x64 `
    -SigningConfigPath C:\Signing\thaiidcardagent-signing.json
```

Development signing with a self-signed certificate (no configuration file; RFC 3161 and trusted
chain default off, and the in-process PowerShell backend is used):

```powershell
.\scripts\Sign-Release.ps1 -PackagePath <package> -CertificateThumbprint <THUMBPRINT> -StoreLocation CurrentUser
```

## Verification

Stage 8 already verifies the ZIP, and `Invoke-ReleaseBuild.ps1` runs the standalone verifier as an
independent confirmation. Verify again on the receiving side:

```powershell
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> `
    -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain `
    -ExpectedSignerThumbprint <THUMBPRINT>
```

What each switch adds:

| Switch | Effect |
| --- | --- |
| `-RequireSigned` | `signingStatus` must be `Signed` and every allowlisted file must be signed |
| `-RequireTimestamp` | every signature must carry a timestamp |
| `-RequireRfc3161Timestamp` | the timestamp must be RFC 3161, not the legacy Authenticode counter-signature |
| `-RequireTrustedChain` | the signing certificate must chain to a root trusted on this machine |
| `-ExpectedSignerThumbprint` / `-ExpectedSignerSubject` | reject a package signed by anyone else |

Without `-RequireSigned`, an `UnsignedPilot` package passes with a loud warning so pilot
verification can proceed.

### Verification is on the embedded signature

Identity, digest algorithm, and timestamp kind are read from the file's **embedded** PKCS#7
signature, not from what `Get-AuthenticodeSignature` reports. Windows resolves a catalog-signed file
as `Valid` and reports the *catalog's* signer even though that signer never signed the file — a
release binary must carry its own embedded signature, and a catalog-only signature is rejected.

## The signing allowlist

[`scripts/signing-allowlist.json`](../scripts/signing-allowlist.json) declares exactly which files
require a signature and which executable content is permitted at all.

| List | Meaning |
| --- | --- |
| `requiredSigned` | Must be present in the payload **and** signed by the release signer |
| `optionalSigned` | If present, must be signed by the release signer |
| `allowedThirdPartySigned` | Executable content permitted to carry a vendor signature instead of ours; must still be signed |
| `allowedUnsigned` | Executable content explicitly permitted to be unsigned. Keep empty for production — every entry is an accepted risk |
| `executableExtensions` | Any payload file with one of these extensions matching none of the lists above is rejected as **unexpected executable content** |

Adding a file to the payload therefore requires a deliberate allowlist change. That is the point:
an unreviewed binary cannot slip into a release.

## What the release records

`release-manifest.json` of a signed package contains, under `signing`:

```json
{
  "signerSubject": "...",
  "signerIssuer": "...",
  "certificateSubject": "...",
  "certificateThumbprint": "...",
  "signatureAlgorithm": "SHA256",
  "timestamped": true,
  "timestampKind": "RFC3161",
  "timestampServer": "...",
  "certificateValidity": { "notBeforeUtc": "...", "notAfterUtc": "..." },
  "verification": {
    "result": "Passed",
    "verifiedAtUtc": "...",
    "requiredFileCount": 1,
    "signedFileCount": 1,
    "allowlistPolicy": "signing-allowlist.json"
  }
}
```

Plus `gitCommit`, `buildTimestampUtc`, and the SHA-256 of every payload file. **No secret is ever
recorded.** Complete the manual part of the release evidence per custody policy section 8.

## When signing fails

Every failure leaves the package `UnsignedPilot`. Nothing is ever marked `Signed` on a partial
success, and there is no override.

| Message | Cause | Action |
| --- | --- | --- |
| `certificate not found ... for the supplied thumbprint` | Token not inserted, CSP/KSP not loaded, wrong store | Insert the token; confirm the certificate is visible in the configured store |
| `does not have an associated private key` | Public-only certificate | Use the certificate whose key is on the token/HSM |
| `does not have the Code Signing EKU` | Wrong certificate type (e.g. a TLS certificate) | Obtain a code signing certificate |
| `Certificate has expired` / `is not yet valid` | Outside the validity window | Renew (custody policy section 6) |
| `expires in ... below the required minimum` | Inside the renewal window | Rotate to the new certificate |
| `Signer mismatch` | Certificate is not the one the release was authorized to use | Confirm the configured thumbprint/subject/issuer |
| `signtool sign failed with exit code ...` | Wrong PIN, token locked, TSA unreachable, provider error | Read the signtool output; do not retry blindly against a lockout counter |
| `Timestamping failed` / `timestamp is 'Legacy'` | TSA unreachable, or the PowerShell backend was used | Confirm the TSA URL; use the SignTool backend |
| `not in the signing allowlist` | Payload gained executable content | Review the new file, then update the allowlist deliberately |
| `missing required signed file` | Publish output incomplete | Investigate the build |
| `forbidden secret-bearing key` | A credential was put in the signing configuration | Remove it; PINs are entered interactively |
| `unresolved placeholder` | Procurement values not filled in | Complete the signing configuration |
| `Final package (ZIP) verification FAILED` | ZIP does not match the signed package | Do not distribute; rebuild and investigate |

## Related

- [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md) — workstream plan and exit criteria.
- [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) — procurement.
- [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) — custody and PIN handling.
- [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md) — the real token/HSM test.
- [RELEASE-PROCESS.md](RELEASE-PROCESS.md) — packaging and install-time integrity.
