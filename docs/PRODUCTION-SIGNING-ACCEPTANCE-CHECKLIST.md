# Production Signing Acceptance Checklist

Operator checklist for the **real hardware token / HSM test**: the acceptance run that proves the
production code signing certificate, its token/HSM key, and the RFC 3161 timestamp service work end
to end, and that the signed release installs on a clean machine.

Production Signing must **not** be declared complete until every section below is Passed or has an
explicitly recorded and accepted **Not Tested**.

Record each item as **Passed / Failed / Not Tested**. A skipped step is **Not Tested**, never
Passed. **Never record a PIN, password, HSM credential, or cardholder data on this sheet.**
Thumbprints, subject DNs, and timestamps are not secrets and should be recorded.

| Field | Value |
| --- | --- |
| Release version | |
| Git commit | |
| Date (UTC) | |
| Release Signing Officer | |
| Release Approver (must differ) | |
| Signing workstation | |
| Token / HSM identifier (non-secret) | |
| Certificate thumbprint | |
| Certificate subject DN | |
| Certificate issuer DN | |
| Timestamp service used | |

---

## A. Prerequisites

| # | Item | Result | Notes |
|---|------|--------|-------|
| A1 | Certificate meets every item in [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) section 7 | | |
| A2 | Private key confirmed generated on, and non-exportable from, the token/HSM (vendor attestation held) | | |
| A3 | Custody policy in force: named signers, custodian, approver recorded | | |
| A4 | Signing workstation is the designated one, patched, disk-encrypted, dedicated | | |
| A5 | `signtool.exe` present (`(Get-Command signtool.exe).Source` or Windows SDK path) | | |
| A6 | Token driver / HSM client installed; token inserted or HSM credential active | | |
| A7 | Certificate visible with private key: `Get-Item Cert:\LocalMachine\My\<THUMBPRINT>` shows `HasPrivateKey = True` | | |
| A8 | Signing configuration filled in from the template, stored outside the repository, ACL restricted | | |
| A9 | Signing configuration contains **no** PIN/password/credential (visually confirmed) | | |
| A10 | Working tree clean at the release commit | | |
| A11 | Release approved in writing by the Release Approver | | |

## B. Certificate validation (before signing anything)

Run on the signing workstation:

```powershell
Import-Module .\scripts\ReleasePackaging.psm1 -Force
$cert = Get-Item Cert:\LocalMachine\My\<THUMBPRINT>
Test-CodeSigningCertificate -Certificate $cert -MinimumRemainingDays 30
$cert | Format-List Subject, Issuer, NotBefore, NotAfter, Thumbprint, HasPrivateKey
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| B1 | `Test-CodeSigningCertificate` returns True (no throw) | | |
| B2 | Code Signing EKU `1.3.6.1.5.5.7.3.3` present | | `($cert.EnhancedKeyUsageList).ObjectId` |
| B3 | `HasPrivateKey = True` | | |
| B4 | Within validity window, and more than 30 days remaining | | record NotAfter |
| B5 | Subject DN matches the configured `expectedSignerSubject` | | |
| B6 | Issuer DN matches the configured `expectedSignerIssuer` | | |
| B7 | Chain builds to a trusted root: `(New-Object System.Security.Cryptography.X509Certificates.X509Chain).Build($cert)` returns True | | |
| B8 | Signing configuration loads: `New-ReleaseSigningOption -ConfigPath <path>` succeeds (no unresolved placeholder) | | |

## C. Fail-closed rehearsal (prove the gates before the real run)

These must **fail**. A gate that does not fire is a Failed item.

| # | Item | Expected | Result | Notes |
|---|------|----------|--------|-------|
| C1 | Sign with the token **removed** / HSM credential inactive | Fails: certificate not found, or signtool error. Package stays `UnsignedPilot` | | |
| C2 | Sign with a deliberately wrong `certificateThumbprint` | Fails: certificate not found | | |
| C3 | Verify with `-ExpectedSignerThumbprint` set to a different thumbprint | Fails: signer mismatch | | |
| C4 | Sign with an unreachable `timestampServerUrl` | Fails: timestamping failed. Package stays `UnsignedPilot` | | |
| C5 | Add a stray `.dll` to the payload, refresh checksums, verify | Fails: unexpected executable content | | |
| C6 | Modify a signed binary, refresh checksums, verify | Fails: HashMismatch | | |
| C7 | Put a `tokenPin` key in a copy of the signing configuration and load it | Fails: forbidden secret-bearing key | | |
| C8 | Load the unmodified `signing-config.template.json` | Fails: unresolved placeholder | | |

After each rehearsal, confirm `release-manifest.json` still reads `"signingStatus": "UnsignedPilot"`.

| # | Item | Result | Notes |
|---|------|--------|-------|
| C9 | No rehearsal failure left the package marked `Signed` | | |

## D. Real signed release

```powershell
.\scripts\Invoke-ReleaseBuild.ps1 -Version '<VERSION>' -SigningConfigPath <config> -WhatIf
.\scripts\Invoke-ReleaseBuild.ps1 -Version '<VERSION>' -SigningConfigPath <config>
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| D1 | `-WhatIf` dry run completes with no side effects (no package folder, no ZIP) | | |
| D2 | Publish succeeded | | |
| D3 | Payload validated: no secrets, no unexpected executable content | | |
| D4 | Token/HSM PIN prompt appeared and was entered **interactively** | | PIN never on a command line |
| D5 | `signtool` signed every allowlisted file (exit code 0) | | |
| D6 | Signature verification stage reported PASSED for every required file | | |
| D7 | Checksums regenerated **after** signing | | |
| D8 | `release-manifest.json` written with signing evidence | | |
| D9 | ZIP created **after** signing | | |
| D10 | Final ZIP verification PASSED | | |
| D11 | Independent `Test-ReleaseSignature.ps1` run PASSED | | |

## E. Signature and timestamp evidence

```powershell
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> `
    -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain `
    -ExpectedSignerThumbprint <THUMBPRINT>

Import-Module .\scripts\ReleasePackaging.psm1 -Force
Get-AuthenticodeSignatureDetail -LiteralPath <package>\app\ThaiIdCardAgent.Service.exe
```

| # | Item | Expected | Result | Notes |
|---|------|----------|--------|-------|
| E1 | Every required file signed | `signedFileCount = requiredFileCount` | | |
| E2 | Embedded signature present (not catalog-only) | `HasSignature = True` | | |
| E3 | Digest algorithm | `SHA256` | | |
| E4 | Timestamp present | `Timestamped = True` | | |
| E5 | Timestamp kind | `RFC3161` | | not `Legacy` |
| E6 | Signer thumbprint matches the production certificate | exact match | | |
| E7 | Chain trusted on the signing workstation | `-RequireTrustedChain` passes | | |
| E8 | `signtool verify /pa /v <exe>` reports a valid signature and timestamp | | | independent tool |

## F. Manifest and secret hygiene

| # | Item | Result | Notes |
|---|------|--------|-------|
| F1 | `signingStatus` is `Signed` | | |
| F2 | `signing.signerSubject` / `signerIssuer` recorded and correct | | |
| F3 | `signing.certificateThumbprint` recorded and correct | | |
| F4 | `signing.signatureAlgorithm` is `SHA256` | | |
| F5 | `signing.timestamped` is `true`, `timestampKind` is `RFC3161` | | |
| F6 | `signing.certificateValidity.notBeforeUtc` / `notAfterUtc` recorded | | |
| F7 | `signing.verification.result` is `Passed` | | |
| F8 | Manifest contains **no** password, PIN, key material, or PFX path | | search the raw JSON |
| F9 | Package contains **no** `.pfx`/`.p12`/`.key`/`.pem`/`.env`/`.log` | | `Test-ReleaseSecretExclusion` |
| F10 | Signing configuration was **not** copied into the package | | |
| F11 | `git status` clean — no key, PFX, config, or artifact staged | | |
| F12 | PowerShell command history on the signing workstation contains no credential | | |

## G. Clean-machine acceptance (from the ZIP alone)

Perform on a clean Windows machine or VM that has never had the agent installed and has no source
tree. Bring only the release ZIP and the acceptance bundle.

| # | Item | Result | Notes |
|---|------|--------|-------|
| G1 | ZIP transferred; SHA-256 matches the value recorded at signing | | |
| G2 | `Test-ReleaseSignature.ps1 -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain` passes **on the clean machine** | | the real trust test |
| G3 | Signing CA chain resolves **without** manually installing any certificate | | |
| G4 | Windows file Properties → Digital Signatures shows the signature, the correct signer, and the timestamp | | |
| G5 | Running the installer/executable shows the **verified publisher** name (not "Unknown publisher") | | record the exact string shown |
| G6 | SmartScreen behaviour recorded | | expected for OV until reputation builds; note whether a warning appeared |
| G7 | `Install-Service.ps1 -PackagePath <package> -RequireSigned` succeeds | | |
| G8 | Service starts and reaches healthy state | | |
| G9 | `Test-PilotDeployment.ps1 -RequireSigned` passes (VerifyOnly, Tamper, Rollback, Full as applicable) | | see [PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md) |
| G10 | Tamper mode still rejects a modified signed binary | | |
| G11 | Upgrade over an installed version succeeds with signatures enforced | | |
| G12 | Uninstall preserves config/logs | | |

## H. Post-expiry durability (the reason for timestamping)

| # | Item | Result | Notes |
|---|------|--------|-------|
| H1 | Timestamp confirmed present on every signed file (E4/E5 passed) | | |
| H2 | TSA certificate validity recorded | | from `signtool verify /pa /v` output |
| H3 | Understood and recorded: signatures remain valid after certificate expiry **because** of the RFC 3161 timestamp | | |
| H4 | Optional simulation: set the clean machine clock past `notAfterUtc` and confirm the signature still validates, then restore the clock | | Not Tested is acceptable; record if skipped |

## I. Evidence and close-out

| # | Item | Result | Notes |
|---|------|--------|-------|
| I1 | Signing log entry completed (custody policy section 8) | | |
| I2 | Custody log updated: token checked out and back in | | |
| I3 | SHA-256 of the released ZIP recorded and published with the release | | |
| I4 | `release-manifest.json` archived with the release evidence | | |
| I5 | Output of the verification runs archived | | |
| I6 | Token returned to controlled storage; workstation left with no token inserted | | |
| I7 | This completed checklist archived | | |
| I8 | [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md) updated | | |
| I9 | Any Failed or Not Tested item has a recorded decision and owner | | |

---

## Sign-off

Production Signing is complete only when sections A–G are Passed (H4 may be Not Tested) and I1–I9
are Passed.

| Role | Name | Date | Signature |
| --- | --- | --- | --- |
| Release Signing Officer | | | |
| Release Approver | | | |
| Security Contact | | | |

**Outcome:** ☐ Production Signing COMPLETE ☐ NOT COMPLETE — blocking items: ______________________

## Related

- [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md)
- [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md)
- [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md)
- [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md)
- [PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md)
