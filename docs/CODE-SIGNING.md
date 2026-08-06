# Code Signing

This document covers Authenticode signing for ThaiIdCardAgent release packages: certificate
requirements, timestamping, PFX handling, certificate-store usage, key rotation, and the
response to a compromised signing certificate.

Signing is performed by [`scripts/Sign-Release.ps1`](../scripts/Sign-Release.ps1) and verified
by [`scripts/Test-ReleaseSignature.ps1`](../scripts/Test-ReleaseSignature.ps1). Both are
Windows PowerShell 5.1 compatible and use the built-in `Set-AuthenticodeSignature` /
`Get-AuthenticodeSignature` (no external `signtool` dependency).

## Unsigned pilot limitation

The current pilot builds are **UnsignedPilot**: the executable, DLLs, installer, and scripts
are not Authenticode signed. Consequences on end-user machines:

- Windows SmartScreen and "Unknown publisher" warnings on first run.
- No cryptographic proof of publisher identity or integrity from the OS.
- PowerShell execution policy may block unsigned scripts (pilot uses an explicit bypass).

Unsigned pilot builds are acceptable **only** for controlled pilot machines with a documented,
out-of-band integrity check (the SHA-256 manifest). They must not be distributed publicly.

## Production signing requirements

To produce a `Signed` package you need a code signing certificate that satisfies **all** of:

| Requirement | Why |
| --- | --- |
| **Code Signing EKU** `1.3.6.1.5.5.7.3.3` | Only certificates issued for code signing are accepted. A localhost/HTTPS (Server Authentication `1.3.6.1.5.5.7.3.1`) certificate is **rejected**. |
| Private key present and usable | Signing requires the private key. |
| Currently valid (NotBefore ≤ now ≤ NotAfter) | Expired or not-yet-valid certificates are rejected. |
| Issued by a CA trusted on target machines (production) | So Windows reports the signature as Valid without manual trust. |

`Test-CodeSigningCertificate` in the shared module enforces the first three and **fails closed**
(stops the run) otherwise. An HTTPS certificate can never be used because it lacks the Code
Signing EKU.

## Timestamping

Always supply `-TimestampServer` for production. A timestamp keeps signatures valid **after**
the signing certificate expires (the timestamp proves the signature existed while the
certificate was valid).

```powershell
-TimestampServer http://timestamp.digicert.com   # or your CA's RFC 3161 server
```

Behaviour:

- If `-TimestampServer` is omitted, signing proceeds with a **warning** that signatures will
  expire with the certificate. Use only for pilot/test.
- If `-TimestampServer` is supplied and timestamping **fails** (unreachable/invalid server),
  the run is **not** reported as Passed — `Sign-Release.ps1` stops. This prevents shipping a
  signature that silently lacks a timestamp.
- Verify timestamps are present with `Test-ReleaseSignature.ps1 -RequireSigned -RequireTimestamp`.

## Certificate store vs PFX

`Sign-Release.ps1` accepts the certificate two ways:

### Windows certificate store (recommended)

```powershell
.\scripts\Sign-Release.ps1 -PackagePath <package> `
    -CertificateThumbprint <THUMBPRINT> -StoreLocation CurrentUser `
    -TimestampServer http://timestamp.digicert.com
```

- No password on disk; the private key stays in the store (ideally non-exportable, or on an
  HSM/token).
- The thumbprint is passed as a parameter — **never hardcoded** in the scripts.

### PFX file with a SecureString password

```powershell
$pw = Read-Host -AsSecureString 'PFX password'
.\scripts\Sign-Release.ps1 -PackagePath <package> `
    -PfxPath C:\secure\signer.pfx -PfxPassword $pw `
    -TimestampServer http://timestamp.digicert.com
```

## PFX handling rules

- The password is a `SecureString`, passed straight to the certificate constructor. It is
  **never** converted to plain text, written to logs/output, or stored in the release manifest.
  (A test asserts the password does not appear in script output.)
- **Never commit** a PFX/P12/private key. `.gitignore` blocks `*.pfx`, `*.p12`, `*.pvk`,
  `*.key`, `*.pem`, `*.snk`.
- Store PFX files outside the repository, on encrypted storage, with least-privilege access.
- Prefer a non-exportable store-based key or an HSM/hardware token over a PFX on disk.

## Verifying signatures

```powershell
.\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> -RequireSigned
```

- Verifies the SHA-256 checksum manifest first (tamper is caught even before signature checks).
- Requires `signingStatus = Signed` and every target file (`ThaiIdCardAgent.Service.exe` and
  the project's own `ThaiIdCardAgent.*.dll`) to have a Valid Authenticode signature.
- Add `-RequireTimestamp` to also require a timestamp on each signature.

## Signing PowerShell scripts

Pilot scripts are unsigned and rely on an execution-policy bypass. For production, sign the
release scripts with the same code signing certificate and set an execution policy of
`AllSigned`/`RemoteSigned` on target machines. `Sign-Release.ps1 -IncludeScripts` signs
`*.ps1`/`*.psm1`/`*.psd1` inside a package payload when scripts are included in it.

## Key rotation

Plan certificate rotation before expiry:

1. Obtain the new code signing certificate (new key pair; do **not** reuse the old key).
2. Install it in the signing store (or produce a new PFX) and validate with
   `Test-CodeSigningCertificate`.
3. Re-sign the current release with the new certificate and a timestamp.
4. Update deployment documentation with the new certificate subject/thumbprint (recorded in
   `release-manifest.json` of signed packages).
5. Keep the old public certificate available so previously timestamped binaries continue to
   verify.
6. Securely destroy the old private key/PFX once no longer needed.

## Compromised signing certificate response

If a signing key/certificate is suspected compromised:

1. **Stop signing** with it immediately and quarantine any PFX copies.
2. **Request revocation** from the issuing CA (revocation applies from the revocation date;
   timestamped signatures created before revocation may still validate — assume the worst).
3. Rotate to a **new** key/certificate (see Key rotation) and re-sign current releases.
4. Notify pilot operators; where feasible, re-deploy re-signed binaries and treat previously
   distributed binaries signed with the compromised certificate as untrusted.
5. Review access logs and tighten access to the signing key (move to HSM/token if not already).
6. Record the incident, affected versions/thumbprint, and remediation.

## Related

- [RELEASE-PROCESS.md](RELEASE-PROCESS.md) — building and verifying packages.
- [SECURITY-BOUNDARIES.md](SECURITY-BOUNDARIES.md) — overall trust boundaries.
- [PRODUCTION-READINESS.md](PRODUCTION-READINESS.md) — readiness checklist.
