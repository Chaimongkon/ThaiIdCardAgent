# Code Signing

Reference for the signing scripts: certificate requirements, timestamping, certificate sources,
the signing allowlist, key rotation, and compromise response.

**Start here instead for a production release:**

| Document | Purpose |
| --- | --- |
| [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md) | Workstream plan, fail-closed conditions, exit criteria |
| [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) | What to procure |
| [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) | Key custody, PIN handling, rotation, revocation |
| [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) | How a signing run is performed |
| [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md) | The real token/HSM acceptance test |

Signing is performed by [`scripts/Sign-Release.ps1`](../scripts/Sign-Release.ps1) and verified by
[`scripts/Test-ReleaseSignature.ps1`](../scripts/Test-ReleaseSignature.ps1);
[`scripts/Invoke-ReleaseBuild.ps1`](../scripts/Invoke-ReleaseBuild.ps1) runs the whole ordered
pipeline. All are Windows PowerShell 5.1 compatible.

Two signing backends:

| Backend | Use | Timestamp |
| --- | --- | --- |
| **SignTool** (`signtool.exe`, Windows SDK) | Production. Required for hardware token / HSM keys and RFC 3161 timestamps | RFC 3161 via `/tr` `/td SHA256` |
| **PowerShell** (`Set-AuthenticodeSignature`) | Development only | Legacy Authenticode counter-signature only — **cannot** satisfy an RFC 3161 requirement, and the script says so rather than producing a weaker release |

## Unsigned pilot limitation

The current pilot builds are **UnsignedPilot**: the executable, DLLs, installer, and scripts
are not Authenticode signed. Consequences on end-user machines:

- Windows SmartScreen and "Unknown publisher" warnings on first run.
- No cryptographic proof of publisher identity or integrity from the OS.
- PowerShell execution policy may block unsigned scripts (pilot uses an explicit bypass).

Unsigned pilot builds are acceptable **only** for controlled pilot machines with a documented,
out-of-band integrity check (the SHA-256 manifest). They must not be distributed publicly.

## Production signing requirements

Full procurement requirements are in
[PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md). What the tooling itself
enforces:

| Requirement | Why | Enforced by |
| --- | --- | --- |
| **Code Signing EKU** `1.3.6.1.5.5.7.3.3` present | Only certificates issued for code signing are accepted. A localhost/HTTPS (Server Authentication `1.3.6.1.5.5.7.3.1`) certificate, or one with no EKU at all, is **rejected** | `Test-CodeSigningCertificate` |
| Private key present and usable | Signing requires the private key | `Test-CodeSigningCertificate` |
| Currently valid (NotBefore ≤ now ≤ NotAfter) | Expired or not-yet-valid certificates are rejected | `Test-CodeSigningCertificate` |
| More than `-MinimumCertificateRemainingDays` left | Stops a release being signed with a certificate about to expire | `Test-CodeSigningCertificate` |
| Subject / issuer / thumbprint match what was configured | A release must be signed by the authorized certificate, not merely by *a* valid one | `Test-CodeSigningCertificate` |
| SHA-256 signature digest | SHA-1 Authenticode signatures are rejected | `Test-ReleaseSignatureFile` |
| Issued by a CA trusted on target machines | So Windows reports the signature as Valid without manual trust | `-RequireTrustedChain` |

All of these **fail closed** — they stop the run rather than downgrade the result.

## Timestamping

Production requires an **RFC 3161** timestamp. It keeps signatures valid **after** the signing
certificate expires, because the timestamp proves the signature existed while the certificate was
valid.

The timestamp service URL is configuration, never a literal in this repository. It is
`<RFC3161_TIMESTAMP_SERVICE_URL>` in
[`scripts/signing-config.template.json`](../scripts/signing-config.template.json) until the actual
service is confirmed during procurement.

```powershell
# Production: from the signing configuration.
.\scripts\Sign-Release.ps1 -PackagePath <package> -SigningConfigPath <config>

# Development: explicit, ad-hoc.
.\scripts\Sign-Release.ps1 -PackagePath <package> -CertificateThumbprint <THUMBPRINT> `
    -TimestampServer <RFC3161_TIMESTAMP_SERVICE_URL>
```

Behaviour:

- No timestamp server and no RFC 3161 requirement: signing proceeds with a **warning** that
  signatures will expire with the certificate. Development and pilot only.
- Timestamping **fails** (unreachable or invalid service): the run stops and the package is **not**
  marked Signed. A signature that silently lacks a timestamp is never shipped.
- `-RequireRfc3161Timestamp` with no timestamp server, or with the PowerShell backend, is refused
  up front rather than producing a legacy timestamp that would quietly fail the requirement.
- Verify with `Test-ReleaseSignature.ps1 -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp`.

## Certificate sources

### Hardware token / HSM through the certificate store (production)

The token or HSM registers the certificate through its CSP/KSP, and it is addressed by thumbprint.
The private key never leaves the device.

```powershell
.\scripts\Sign-Release.ps1 -PackagePath <package> -SigningConfigPath <config>
```

`signtool` raises the PIN prompt itself. **The PIN is entered interactively** and never passes
through these scripts or any command line.

### Windows certificate store by thumbprint

```powershell
.\scripts\Sign-Release.ps1 -PackagePath <package> `
    -CertificateThumbprint <THUMBPRINT> -StoreLocation CurrentUser
```

The thumbprint is a parameter — **never hardcoded** in the scripts.

### PFX file with a SecureString password (development only)

```powershell
$pw = Read-Host -AsSecureString 'PFX password'
.\scripts\Sign-Release.ps1 -PackagePath <package> -PfxPath C:\secure\dev-signer.pfx -PfxPassword $pw
```

- The PFX is loaded with `EphemeralKeySet` — **never** `Exportable`. The private key is not
  extractable and is not persisted.
- The password is a `SecureString` passed straight to the certificate constructor. It is **never**
  converted to plain text, written to logs/output, or stored in the manifest. A test asserts it
  never appears in script output.
- PFX signing through `signtool` is **refused**: it would require the password as a `/p` command-line
  argument, which the credential-handling rules forbid.
- **Never commit** a PFX/P12/private key. `.gitignore` blocks `*.pfx`, `*.p12`, `*.pvk`, `*.key`,
  `*.pem`, `*.snk`, and `Test-ReleaseSecretExclusion` refuses to package one.
- A production release is **never** signed with a PFX.

## The signing allowlist

[`scripts/signing-allowlist.json`](../scripts/signing-allowlist.json) is the authority on which
files require a signature and which executable content may be in a package at all:

| List | Meaning |
| --- | --- |
| `requiredSigned` | Must be present **and** signed by the release signer |
| `optionalSigned` | If present, must be signed by the release signer |
| `allowedThirdPartySigned` | May carry a vendor signature instead of ours; must still be signed |
| `allowedUnsigned` | Explicitly permitted unsigned executable content. Keep empty for production |
| `executableExtensions` | Any payload file with one of these extensions matching none of the above is rejected as **unexpected executable content** |

The allowlist is checked when the package is built, before signing, and again at verification.

## Verifying signatures

```powershell
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> `
    -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain `
    -ExpectedSignerThumbprint <THUMBPRINT>
```

- Verifies the SHA-256 checksum manifest first (tamper is caught even before signature checks).
- Enforces the signing allowlist: no missing required file, no unexpected executable content.
- Requires `signingStatus = Signed` and a valid signature on every allowlisted file.

**Verification reads the embedded signature.** Identity, digest algorithm, and timestamp kind come
from the file's embedded PKCS#7 signature, not from what `Get-AuthenticodeSignature` reports.
Windows resolves a catalog-signed file as `Valid` and reports the *catalog's* signer even though
that signer never signed the file, so a catalog-only signature is rejected for a release binary.

## Signing PowerShell scripts

Pilot scripts are unsigned and rely on an execution-policy bypass. For production, sign the release
scripts with the same code signing certificate and set an execution policy of
`AllSigned`/`RemoteSigned` on target machines. `*.ps1`/`*.psm1`/`*.psd1` inside a package payload
are covered by the `optionalSigned` list, so they are signed and verified automatically when
present.

## Key rotation and compromise response

Both are governed by [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md), which carries
the binding timeline, roles, and steps. In summary:

**Rotation** starts 90 days before expiry, always generates a **new key pair** on the token/HSM
(never re-keys the old one), validates the new certificate with `Test-CodeSigningCertificate`,
switches the signing configuration to the new thumbprint, re-signs releases still in distribution,
retains the old **public** certificate so previously timestamped binaries keep verifying, and
destroys the old private key with the destruction recorded.

**Compromise response** stops signing immediately, requests revocation from the CA with an accurate
compromise date, enumerates affected releases from the `certificateThumbprint` recorded in each
`release-manifest.json`, rotates to a new key on a rebuilt workstation, re-signs and redistributes,
notifies deployment sites that binaries carrying the compromised thumbprint are untrusted, and
records the incident. Timestamped signatures created before the revocation date may still validate —
plan for redistribution rather than relying on revocation alone.

## Related

- [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md) — workstream plan and exit criteria.
- [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) — procurement.
- [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) — custody, PIN handling, rotation.
- [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) — the signing procedure.
- [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md) — real token/HSM test.
- [RELEASE-PROCESS.md](RELEASE-PROCESS.md) — building and verifying packages.
- [SECURITY-BOUNDARIES.md](SECURITY-BOUNDARIES.md) — overall trust boundaries.
- [PRODUCTION-READINESS.md](PRODUCTION-READINESS.md) — readiness checklist.
