# Production Signing — Implementation Plan

This is a **separate workstream** that must complete before Phase 13 (Thai Card Provider
Integration) begins. Its goal is a ThaiIdCardAgent release that is Authenticode signed with an
organizational code signing certificate whose private key lives on a hardware token or HSM, is
SHA-256 signed, is RFC 3161 timestamped, and passes clean-machine acceptance.

> **Status: NOT COMPLETE.** The tooling, policy, tests, and checklists in this iteration are
> finished and verified with test certificates. Production Signing is not complete and must not be
> claimed complete until a real code signing certificate and hardware token/HSM have been obtained
> and a real timestamped signed release passes clean-machine acceptance (see
> [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md)).

## Documents in this workstream

| Document | Purpose |
| --- | --- |
| PRODUCTION-SIGNING-PLAN.md (this file) | Scope, phases, current state, exit criteria |
| [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) | Procurement requirements for the certificate and timestamp service |
| [SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) | Key custody, authorized personnel, PIN handling, rotation, revocation |
| [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) | The operational signing procedure and stage order |
| [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md) | Operator checklist for the real token/HSM test |
| [CODE-SIGNING.md](CODE-SIGNING.md) | Reference for the signing scripts and their parameters |

## Phases

### Phase S1 — Tooling, policy, and tests (COMPLETE, verified with test certificates)

Delivered in this iteration:

- **Signing allowlist** ([`scripts/signing-allowlist.json`](../scripts/signing-allowlist.json)):
  declares exactly which payload files must be signed, which may be, which executable content may
  carry a third-party signature or none, and which extensions count as executable. Anything
  executable that matches no list is rejected as unexpected executable content.
- **Signing configuration template**
  ([`scripts/signing-config.template.json`](../scripts/signing-config.template.json)): certificate
  identity, RFC 3161 timestamp URL, and provider descriptors as `<PLACEHOLDER>` values. It contains
  no secret and is rejected while any required placeholder is unresolved.
- **Stage-ordered pipeline**
  ([`scripts/Invoke-ReleaseBuild.ps1`](../scripts/Invoke-ReleaseBuild.ps1)): publish → sign →
  verify signatures/timestamps → checksums → manifest → ZIP → verify final package.
  `New-ReleasePackage.ps1 -SkipZip` guarantees no unsigned ZIP is ever written for a signed release.
- **signtool backend** for production (SHA-256 file digest, RFC 3161 timestamp via `/tr` `/td`,
  hardware token / HSM keys addressed through the certificate store and their CSP/KSP). The
  in-process PowerShell backend remains available for development only and is explicitly refused
  when an RFC 3161 timestamp is required, because `Set-AuthenticodeSignature` can only apply the
  legacy Authenticode timestamp.
- **Embedded-signature verification**: identity, digest algorithm, and timestamp kind are read from
  the file's embedded PKCS#7 signature, not from `Get-AuthenticodeSignature`'s reported signer.
  Windows resolves catalog-signed files as Valid and reports the *catalog's* signer even though
  that signer never signed the file; a release binary must carry its own embedded signature.
- **Fail-closed gates** for every condition listed under "Fail-closed conditions" below.
- **Manifest signing evidence** in `release-manifest.json` — signer subject/issuer, thumbprint,
  signature algorithm, timestamped flag and kind, certificate validity window, and the verification
  result. No secret is recorded.
- **Tests**: 68 tests in `tests/ThaiIdCardAgent.Release.Tests` pass, including the full set
  required for this workstream (see "Test coverage" below).

### Phase S2 — Procurement (NOT STARTED — blocked on the organization)

1. Approve the requirements in [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md).
2. Select a certificate provider and confirm the RFC 3161 timestamp service URL they operate.
3. Complete organization validation with the CA.
4. Receive the hardware token / provision the HSM key. The private key is generated **on** the
   token/HSM and never exists outside it.
5. Record the non-secret facts (subject DN, issuer DN, thumbprint, validity window, token/HSM
   model, CSP/KSP name) in the signing configuration copy held on the signing workstation.

Nothing in this repository names a vendor or a timestamp URL: both stay `<PLACEHOLDER>` values
until procurement confirms them.

### Phase S3 — Signing workstation setup (NOT STARTED)

1. Dedicated, hardened Windows workstation; Windows SDK signing tools installed.
2. Token drivers / HSM client installed; certificate visible in `Cert:\LocalMachine\My` (or
   `CurrentUser\My`) with its private key on the token/HSM.
3. Copy `scripts/signing-config.template.json` outside the repository, fill in the confirmed
   values, restrict its ACL to the authorized signers.
4. Dry run: `Invoke-ReleaseBuild.ps1 -WhatIf`, then a full signed build of a throwaway version.

### Phase S4 — Real signed release and clean-machine acceptance (NOT STARTED)

Execute [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md)
end to end on a clean machine that has never had the agent installed.

### Phase S5 — Close out (NOT STARTED)

Update [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md), attach the release evidence, and only
then declare Production Signing complete. Phase 13 may begin after this point.

## Fail-closed conditions

`Sign-Release.ps1` refuses to produce a Signed package, and `Test-ReleaseSignature.ps1` refuses to
pass, when any of these hold:

| Condition | Enforced by |
| --- | --- |
| Missing certificate (no thumbprint, or not found in the store) | `Sign-Release.ps1` |
| Missing private key | `Test-CodeSigningCertificate` |
| Wrong or missing Code Signing EKU `1.3.6.1.5.5.7.3.3` | `Test-CodeSigningCertificate` |
| Expired or not-yet-valid certificate | `Test-CodeSigningCertificate` |
| Certificate within the renewal window (`minimumCertificateRemainingDays`) | `Test-CodeSigningCertificate` |
| Signer mismatch (subject / issuer / thumbprint) | `Test-CodeSigningCertificate`, `Test-ReleaseSignatureFile` |
| Timestamping failed, or timestamp missing when required | `Sign-Release.ps1`, `Test-ReleaseSignatureFile` |
| Legacy timestamp when RFC 3161 is required | `Test-ReleaseSignatureFile` |
| Invalid signature, or embedded signature fails its cryptographic check | `Test-ReleaseSignatureFile` |
| Digest algorithm weaker than SHA-256 | `Test-ReleaseSignatureFile` |
| Hash mismatch (file modified after signing) | `Test-ReleaseSignatureFile`, checksum manifest |
| Unsigned required file | `New-ReleaseSigningReport` |
| Unexpected executable content in the payload | `Resolve-ReleaseSigningPlan` |
| Required file missing from the payload | `Resolve-ReleaseSigningPlan` |
| Secret-bearing key in the signing configuration | `New-ReleaseSigningOption` |
| Credential-carrying signtool argument | `Test-SignToolArgumentSafety` |
| Unresolved `<PLACEHOLDER>` in the signing configuration | `New-ReleaseSigningOption` |
| Final ZIP does not verify after signing | `Test-ReleaseZipIntegrity` |

## Prohibitions (enforced, not merely documented)

- The private key is never exported. PFX loading uses `EphemeralKeySet`, never `Exportable`, and
  PFX signing through signtool is refused because it would require the password on a command line.
- No PIN or password is ever accepted in source, configuration, environment files, command
  history, logs, test output, or the package. `New-ReleaseSigningOption` rejects secret-looking
  configuration keys; `Test-SignToolArgumentSafety` rejects credential-carrying arguments; the token
  PIN is entered interactively into the CSP/KSP prompt and never passes through these scripts.
- PFX/P12/key files are excluded from Git (`.gitignore`) and refused inside a package
  (`Test-ReleaseSecretExclusion`).
- No signature or certificate validation may be bypassed. There is no override switch that turns a
  failed verification into a pass.
- A release is marked `Signed` only when every required signature verified. Any failure leaves the
  package `UnsignedPilot` and stops the run.

## Test coverage

All in `tests/ThaiIdCardAgent.Release.Tests` (`SigningTests.cs`, `SigningPolicyTests.cs`).

| Required case | Test |
| --- | --- |
| Correct Code Signing EKU | `CodeSigningCertificate_WithCodeSigningEku_IsAccepted` |
| Wrong EKU (Server Authentication) | `CodeSigningCertificate_ServerAuthOnly_IsRejected` |
| Missing EKU entirely | `Certificate_WithNoEkuAtAll_IsRejected` |
| No private key | `Certificate_WithoutPrivateKey_IsRejected` |
| Expired certificate | `CodeSigningCertificate_Expired_IsRejected` |
| Not-yet-valid certificate | `Certificate_NotYetValid_IsRejected` |
| Certificate too close to expiry | `Certificate_TooCloseToExpiry_IsRejected` |
| Timestamp failure | `Sign_WithUnreachableTimestampServer_DoesNotReportPassed`, `RequireRfc3161Timestamp_WithoutTimestampServer_RefusesToSign`, `SignedPackageWithoutTimestamp_IsRejectedWhenTimestampIsRequired`, `PowerShellBackend_CannotSatisfyRfc3161Requirement` |
| Unsigned required binary | `UnsignedRequiredBinary_InAnOtherwiseSignedPackage_IsRejected`, `TestReleaseSignature_RequireSigned_RejectsUnsignedPackage` |
| Tampered signed binary | `SignedThenTampered_SignatureVerification_IsRejected`, `GeneratedPackage_ZipVerification_PassesAndCatchesTampering` |
| Wrong signer | `WrongSigner_IsRejectedByVerification`, `Certificate_WrongSignerThumbprint_IsRejected` |
| Manifest signing metadata | `Manifest_RecordsCompleteSigningEvidence_WithoutSecrets`, `Manifest_UnsignedPilot_HasNoSigningEvidence` |
| Secret exclusion | `SecretExclusion_PfxAndPrivateKey_AreDetected`, `NewReleasePackage_WithSecretInPublish_RefusesToPackage`, `Sign_WithPfxPassword_DoesNotLogThePassword`, `SigningConfig_WithSecretBearingKey_IsRejected`, `SignToolArguments_CarryingACredential_AreRejected` |
| Generated package verification | `GeneratedPackage_ZipVerification_PassesAndCatchesTampering`, `NewReleasePackage_SkipZip_DefersTheZipUntilAfterSigning` |
| Allowlist enforcement | `Allowlist_UnexpectedExecutableInPayload_IsRejectedAtVerification`, `Allowlist_UnexpectedExecutableInPublish_RefusesToPackage`, `Allowlist_MissingRequiredFile_IsRejected`, `Allowlist_IsLoadedFromDisk_AndFailsClosedWhenMissing` |
| Signature inspection | `SignatureDetail_ReportsDigestAlgorithmAndTimestampKind`, `Sha1Signature_IsRejectedWhenSha256IsRequired`, `CatalogSignedFileWithoutEmbeddedSignature_IsNotAcceptedAsSigned` |
| Configuration hygiene | `SigningConfig_WithUnresolvedPlaceholder_IsRejected`, `SigningConfig_MissingCertificateThumbprint_IsRejected` |

**What the tests cannot prove.** They run against ephemeral self-signed certificates in
`Cert:\CurrentUser\My`. They do not exercise a hardware token PIN prompt, an HSM CSP/KSP, a real
CA chain, a real RFC 3161 timestamp service, or SmartScreen reputation. Those are exactly what
Phase S4 covers.

## Exit criteria

Production Signing is complete only when all of these hold:

1. An organizational code signing certificate meeting
   [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) has been issued, with
   its private key on a hardware token or HSM.
2. A real release has been signed on the signing workstation with SHA-256 and a real RFC 3161
   timestamp, and `Test-ReleaseSignature.ps1 -RequireSigned -RequireTimestamp
   -RequireRfc3161Timestamp -RequireTrustedChain` passes.
3. `release-manifest.json` records the signing evidence and contains no secret.
4. The clean-machine acceptance checklist passes with every item Passed or an explicitly recorded
   Not Tested.
5. The key custody policy is in force: named signers, PIN handling, rotation and revocation
   procedures agreed and recorded.
