# Production Signing — Procurement Requirements

Requirements a code signing certificate and timestamp service must meet before they may be used to
sign a ThaiIdCardAgent release. Use this document to brief procurement and to evaluate offers.

> **No vendor is named and no timestamp URL is stated anywhere in this repository.** The certificate
> provider and the RFC 3161 timestamp service are recorded as `<PLACEHOLDER>` values in
> [`scripts/signing-config.template.json`](../scripts/signing-config.template.json) and are filled
> in only once the actual provider and service have been confirmed. The tooling refuses to sign
> while a required placeholder is unresolved.

## 1. Certificate type

| Requirement | Value | Rationale |
| --- | --- | --- |
| Subject type | **Organization** (not individual) | The publisher shown to users is the organization, and key custody is an organizational responsibility. |
| Subject DN | Legal organization name, exactly as validated by the CA | It becomes the "Verified publisher" string in Windows prompts. |
| Validation level | Organization Validation (OV) minimum; Extended Validation (EV) if immediate SmartScreen reputation matters | OV is sufficient for Authenticode; EV additionally grants immediate SmartScreen reputation and is mandatory for kernel-mode drivers (not applicable here). |
| Certificate class | Code signing (Authenticode) | Not a TLS/server certificate. |

**Decision to record during procurement:** OV or EV. If unsigned-publisher SmartScreen warnings on
first run are unacceptable to the pilot sites, choose EV. Record the decision and the reason.

## 2. Mandatory technical properties

| # | Requirement | Detail | Verified by |
| --- | --- | --- | --- |
| T1 | **Extended Key Usage `1.3.6.1.5.5.7.3.3`** (Code Signing) must be present | A TLS/Server Authentication certificate (`1.3.6.1.5.5.7.3.1`) is **rejected**, as is a certificate with no EKU at all | `Test-CodeSigningCertificate` |
| T2 | Key Usage includes Digital Signature | Required for Authenticode | CA profile |
| T3 | **Signature/digest algorithm SHA-256** or stronger | SHA-1 Authenticode signatures are rejected | `Test-ReleaseSignatureFile` |
| T4 | RSA 3072-bit minimum, or ECDSA P-256 minimum | Current CA/Browser Forum code signing baseline | CA profile |
| T5 | Chains to a root trusted by default in the Windows Trusted Root Program | Otherwise every target machine needs manual trust configuration | Clean-machine acceptance |
| T6 | Private key generated **on** and non-exportable **from** a hardware token or HSM | See section 4 | Custody attestation |
| T7 | Validity period as issued by the CA (typically 1–3 years) | Longer validity reduces rotation frequency; timestamping makes signatures outlive expiry | CA quote |
| T8 | Revocation information published (CRL and/or OCSP) reachable from target networks | Required so a compromised certificate can actually be revoked and honoured | Section 6 |

## 3. RFC 3161 timestamping

| # | Requirement | Detail |
| --- | --- | --- |
| S1 | The provider must operate (or name) an **RFC 3161** timestamp service | The legacy Authenticode timestamp protocol is not acceptable. `signtool /tr` is used, never `/t`. |
| S2 | The timestamp service must support a **SHA-256** timestamp digest | Applied via `signtool /td SHA256`. |
| S3 | The TSA certificate must chain to a trusted root and have a long validity | The timestamp is what keeps signatures valid after the signing certificate expires. |
| S4 | Documented availability and rate limits | Signing fails closed if timestamping fails, so TSA downtime blocks releases. |
| S5 | The service URL must be recorded in the signing configuration as `timestampServerUrl` | Left as `<RFC3161_TIMESTAMP_SERVICE_URL>` until confirmed. |

**Why this is non-negotiable:** without a timestamp, every signature becomes invalid the moment the
certificate expires, including on already-deployed machines. With an RFC 3161 timestamp, the
signature remains valid because the timestamp proves it was created while the certificate was valid.

## 4. Private-key storage: hardware token or HSM

The private key must never exist as a file. Acceptable options, in order of preference:

| Option | Description | Notes |
| --- | --- | --- |
| **Cloud HSM / cloud signing service** | Key generated in a FIPS 140-2 Level 3 (or 140-3 equivalent) HSM operated by the provider; signing is performed by the service | Best availability and auditability; usually integrates with `signtool` via a provider-supplied CSP/KSP or `/dlib`. Record the integration method during procurement. |
| **On-premises HSM** | Key generated in an organization-operated HSM | Full control; requires HSM operations capability. |
| **Hardware token (USB)** | Key generated on a FIPS 140-2 Level 2+ token shipped by the CA | Simplest; the token is a single physical point of failure and must be secured and backed by a documented replacement path. |

Mandatory in all cases:

- The key pair is **generated on** the token/HSM. A key generated elsewhere and imported is not
  acceptable.
- The key is **non-exportable**. Obtain the CA's or vendor's attestation of this.
- Access is gated by a PIN or an equivalent authentication factor.
- A lockout policy exists after repeated failed PIN attempts, and the unlock/replacement procedure
  is documented before the token is put into use.

**Integration requirement to confirm with the provider:** how the key is addressed from
`signtool.exe` on Windows — a CSP/KSP that surfaces the certificate in `Cert:\LocalMachine\My`
(the path this repository's tooling uses via `/sha1` + `/sm`), or a provider `/dlib` library.
Record the answer in the `keyCustody` block of the signing configuration.

## 5. Personnel and custody

Detailed in [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md). Procurement must
establish:

- Named **Release Signing Officers** authorized to operate the token/HSM (at least two, so a single
  absence does not block releases).
- A named **Signing Key Custodian** responsible for physical custody and the PIN.
- Whether the provider supports per-user credentials and an audit log (required for cloud HSM).

## 6. Revocation and support

| # | Requirement |
| --- | --- |
| R1 | Documented revocation request procedure, and the maximum time from request to published revocation |
| R2 | 24/7 or documented business-hours support contact for compromise reporting |
| R3 | Reissue/replacement terms if a token is lost, damaged, or locked out |
| R4 | Renewal terms and lead time (needed to plan rotation before expiry) |

## 7. Evaluation checklist

Record the answer for each item before selecting a provider. Leave the vendor column blank in this
repository; it belongs in the procurement record, not in source control.

| # | Item | Required | Confirmed |
| --- | --- | --- | --- |
| 1 | Organization Validation (or EV) code signing certificate | Yes | |
| 2 | EKU `1.3.6.1.5.5.7.3.3` present | Yes | |
| 3 | SHA-256 signing supported | Yes | |
| 4 | RSA ≥ 3072 or ECDSA ≥ P-256 | Yes | |
| 5 | Chains to a default-trusted Windows root | Yes | |
| 6 | Key generated on, and non-exportable from, token/HSM | Yes | |
| 7 | Non-exportability attestation available | Yes | |
| 8 | RFC 3161 timestamp service operated or named | Yes | |
| 9 | Timestamp service supports SHA-256 | Yes | |
| 10 | Timestamp service availability/rate limits documented | Yes | |
| 11 | `signtool` integration method documented (CSP/KSP or `/dlib`) | Yes | |
| 12 | Revocation procedure and turnaround documented | Yes | |
| 13 | Token/HSM replacement procedure documented | Yes | |
| 14 | Renewal lead time documented | Yes | |
| 15 | Per-signer credentials and audit log (cloud HSM) | If cloud | |
| 16 | EV vs OV decision recorded with rationale | Yes | |

## 8. Values to record once confirmed

These go into the signing configuration copy held on the signing workstation — **not** into this
repository:

| Configuration key | Value to record |
| --- | --- |
| `certificateThumbprint` | SHA-1 thumbprint of the issued certificate |
| `expectedSignerSubject` | Exact subject DN as issued |
| `expectedSignerIssuer` | Exact issuer DN of the issuing CA |
| `timestampServerUrl` | Confirmed RFC 3161 endpoint |
| `storeLocation` | `LocalMachine` or `CurrentUser`, per the CSP/KSP |
| `signToolPath` | Absolute path to `signtool.exe`, if not on `PATH` |
| `additionalSignToolArguments` | Provider-specific arguments — **never** a PIN or password |
| `keyCustody.privateKeyStorage` | `HardwareToken`, `HSM`, or `CloudHSM` |
| `keyCustody.tokenOrHsmModel` | Model designation |
| `keyCustody.cryptographicProvider` | CSP/KSP name |
| `keyCustody.authorizedSignerRole` | Role name of the authorized signers |

## Related

- [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md) — workstream plan and exit criteria.
- [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) — custody, PIN handling, rotation.
- [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) — the signing procedure.
