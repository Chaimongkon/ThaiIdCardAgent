# Signing Key Custody and Security Policy

Binding rules for the ThaiIdCardAgent code signing private key: who may use it, where it lives, how
its PIN is handled, how it is rotated, and what happens when it is compromised.

This policy applies from the moment the certificate is issued. Where it says **must**, the release
tooling enforces it and will fail closed; where it says **must (procedural)**, enforcement is
organizational and the control is a documented human step.

## 1. Roles

| Role | Responsibility | Minimum count |
| --- | --- | --- |
| **Signing Key Custodian** | Physical custody of the hardware token / HSM credential. Holds the PIN. Maintains the custody log. | 1 (plus 1 named deputy) |
| **Release Signing Officer** | Authorized to perform a signing run. Operates the signing workstation and enters the PIN. | 2 |
| **Release Approver** | Approves that a specific version may be signed and released. Must not be the same person as the Signing Officer for that release. | 1 |
| **Security Contact** | Receives compromise reports, initiates revocation. | 1 (plus 1 named deputy) |

Record the named holders of each role outside this repository, in the organization's access
register. Names, phone numbers, and PINs do not belong in Git.

## 2. Authorized signing personnel

- Only a named **Release Signing Officer** may run a production signing operation.
- Authorization is granted in writing by the Security Contact and is revoked immediately when the
  person changes role or leaves.
- The list of authorized signers is reviewed at least every 6 months, and always after any
  personnel change.
- A signing run requires a **Release Approver** distinct from the Signing Officer. One person must
  never both approve and sign the same release.
- Every signing run is recorded in the release evidence (section 8).

## 3. Private-key storage

- The signing private key **must** be generated on, and be non-exportable from, a hardware token or
  HSM (see [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) section 4).
- The private key **must never** be exported, backed up as a file, copied, emailed, or placed on
  shared storage. There is no such thing as an authorized copy of the private key.
- A PFX/P12 **must not** be used for production signing. The `-PfxPath` path in the tooling exists
  for development certificates only, loads with `EphemeralKeySet` (never `Exportable`), and is
  refused for signtool-based signing because that would require the password on a command line.
- PFX/P12/PVK/SNK/`.key`/`.pem` files **must not** be committed to Git. `.gitignore` blocks them and
  `Test-ReleaseSecretExclusion` refuses to build a package containing one.
- When not in use, the hardware token **must (procedural)** be stored in a locked safe or equivalent
  controlled storage, separate from the PIN.
- The signing workstation is dedicated to signing: no general browsing, no email, no development
  work, patched, disk-encrypted, and with the token removed except during a signing run.

## 4. PIN and credential handling

The token PIN, HSM credential, and any development PFX password are secrets subject to all of the
following. **All are enforced by the tooling where enforcement is technically possible.**

A PIN or password **must never** appear in:

| Location | Enforcement |
| --- | --- |
| Source code | Code review; no parameter accepts a plaintext credential |
| Configuration files | `New-ReleaseSigningOption` rejects any secret-looking key (`password`, `pin`, `secret`, `token`, `credential`, `apikey`, …) |
| Environment variables / `.env` files | Policy + `Test-ReleaseSecretExclusion` refuses `.env`/`.env.local` in a package |
| Command lines / command history | `Test-SignToolArgumentSafety` rejects `/p`, `/pin`, `--password`, `/kp`, `/csppin`, `/du`, and any `key=value` argument that looks like a credential |
| Logs and script output | The PFX password is a `SecureString` passed straight to the certificate constructor and never converted to text; a test asserts it never appears in output |
| Test output | `Sign_WithPfxPassword_DoesNotLogThePassword` |
| The release package or manifest | `New-ReleaseMetadata` records only non-secret evidence; `Manifest_RecordsCompleteSigningEvidence_WithoutSecrets` asserts it |
| Screenshots, tickets, chat, email | Policy |

Operational rules:

- The token PIN is entered **interactively** at the CSP/KSP prompt raised by `signtool`. It never
  passes through this repository's scripts. This is why `additionalSignToolArguments` may not carry
  a credential.
- The PIN is known to the Signing Key Custodian and the authorized Release Signing Officers, and to
  nobody else.
- The PIN is changed when any person who knew it leaves the role.
- PIN retry lockout is left enabled. The unlock/replacement procedure is documented before the token
  enters service.
- If a PIN is suspected exposed, treat it as a key compromise (section 7) unless it can be changed
  before any unauthorized use is possible, and record the incident either way.

## 5. Use of the key

- The key is used **only** to sign ThaiIdCardAgent release artifacts named by
  [`scripts/signing-allowlist.json`](../scripts/signing-allowlist.json).
- Signing runs happen only on the designated signing workstation, only from a release build produced
  by [`scripts/Invoke-ReleaseBuild.ps1`](../scripts/Invoke-ReleaseBuild.ps1).
- Test builds, development builds, and personal experiments are **never** signed with the production
  key. Use a self-signed development certificate.
- Signature and certificate validation **must never** be bypassed. There is no override switch, and
  none may be added.
- A release is marked `Signed` only when every required signature verified.

## 6. Certificate renewal and key rotation

Rotation is planned, not reactive.

**Timeline**

| When | Action |
| --- | --- |
| Expiry − 90 days | Security Contact opens the renewal with the CA; confirm CA lead time |
| Expiry − 60 days | New key pair generated on the token/HSM (never reuse the old key); new certificate issued |
| Expiry − 45 days | New certificate installed on the signing workstation; validated with `Test-CodeSigningCertificate`; test release signed and verified |
| Expiry − 30 days | Signing configuration switched to the new thumbprint/subject/issuer. `minimumCertificateRemainingDays: 30` makes the tooling refuse the old certificate from this point |
| Expiry − 14 days | Current shipping release re-signed with the new certificate if it is still being distributed |
| Expiry | Old token/key retired per section 6.1 |

**Rules**

- A new certificate always means a **new key pair**. Never re-key onto an existing private key.
- The old **public** certificate is retained indefinitely so previously timestamped binaries
  continue to verify.
- The new certificate subject/thumbprint is recorded in the signing configuration and appears in
  `release-manifest.json` of releases signed with it.
- Deployment documentation and any allowlisting at pilot sites is updated with the new thumbprint
  before the switch.

### 6.1 Retiring an old key

- Old token: return to the CA if required, otherwise destroy it per the vendor's procedure and record
  the destruction (date, serial, witness).
- Old HSM key: destroy the key object; record the operation.
- Never simply "leave it in the drawer": an un-destroyed key is a live signing capability.

## 7. Revocation and compromise response

Treat as a compromise: a lost or stolen token, a token found unattended outside its storage, a
suspected malware infection of the signing workstation, an exposed PIN, an unexplained signature, or
any signing run nobody can account for.

**Immediately (hour 0):**

1. **Stop signing.** Remove the token / disable the HSM credential. Quarantine any development PFX.
2. Notify the Security Contact and the Release Approver.
3. Preserve evidence: signing workstation logs, the custody log, the release evidence records. Do not
   wipe the workstation yet.

**Within 24 hours:**

4. **Request revocation** from the issuing CA. Supply the compromise date if known — revocation
   takes effect from the revocation date, so an accurate date determines which signatures survive.
5. Determine the exposure window and enumerate every release signed inside it, using the
   `certificateThumbprint` and `buildTimestampUtc` recorded in each `release-manifest.json`.
6. Assume the worst about timestamped signatures created before the revocation date: they may still
   validate on target machines even after revocation. Plan for re-signing and redistribution rather
   than relying on revocation alone.

**Within 5 business days:**

7. Rotate to a **new** key and certificate (section 6), on a rebuilt signing workstation.
8. Re-sign and redistribute all currently supported releases with the new certificate.
9. Notify pilot operators and deployment sites: state the affected versions and thumbprint, and
   instruct them to treat binaries carrying the compromised thumbprint as untrusted.
10. Review and tighten access: who had physical access, who knew the PIN, what the workstation was
    exposed to.

**Close out:**

11. Record the incident: what happened, the exposure window, affected versions and thumbprints, the
    remediation, and the preventive change made. Keep it with the release evidence.

## 8. Audit and release evidence

Every production signing run produces evidence that must be retained for the life of the release
plus 2 years. None of it is secret.

**Automatically recorded** in `release-manifest.json`:

- `signingStatus`, signer subject and issuer, certificate thumbprint
- `signatureAlgorithm`, `timestamped`, `timestampKind`, `timestampServer`
- `certificateValidity.notBeforeUtc` / `notAfterUtc`
- `verification.result`, `verifiedAtUtc`, `requiredFileCount`, `signedFileCount`, `allowlistPolicy`
- `gitCommit` and `buildTimestampUtc`, plus the SHA-256 of every payload file

**Recorded manually** in the signing log (outside this repository):

| Field | Note |
| --- | --- |
| Release version and git commit | Must match the manifest |
| Date/time of the signing run (UTC) | |
| Release Signing Officer | Named person |
| Release Approver | Different named person |
| Token/HSM serial or key identifier | Non-secret identifier only |
| Certificate thumbprint used | Must match the manifest |
| Timestamp service used | |
| Verification outcome | Output of `Test-ReleaseSignature.ps1 -RequireSigned -RequireTimestamp -RequireRfc3161Timestamp -RequireTrustedChain` |
| SHA-256 of the released ZIP | Distributed alongside the release |
| Anomalies | Anything unexpected, however minor |

**Custody log** (token check-out/check-in): date, person, purpose, check-out time, check-in time,
witnessed by. Reviewed by the Security Contact at least quarterly.

**Review cadence:**

| Item | Frequency |
| --- | --- |
| Authorized signer list | Every 6 months and on any personnel change |
| Custody log | Quarterly |
| Signing log vs released artifacts (every released binary maps to a logged run) | Quarterly |
| This policy | Annually, and after any incident |

## 9. Prohibited actions

Never, under any circumstance or deadline pressure:

1. Export the private key, or create a file-based copy of it.
2. Commit a PFX/P12/PVK/SNK/`.key`/`.pem` or any credential to Git.
3. Store a PIN or password in source, configuration, environment files, command history, logs, test
   output, or a release package.
4. Pass a PIN or password as a command-line argument.
5. Bypass, disable, or "temporarily skip" signature or certificate validation.
6. Mark a release `Signed` when any required signature failed.
7. Sign an artifact that was not produced by the project's release build.
8. Sign from a machine other than the designated signing workstation.
9. Share the token, PIN, or HSM credential with anyone not on the authorized signer list.
10. Continue signing after a suspected compromise.

## Related

- [PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) — what to procure.
- [RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) — how a signing run is performed.
- [PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md) — the
  real token/HSM acceptance test.
- [SECURITY-BOUNDARIES.md](SECURITY-BOUNDARIES.md) — overall trust boundaries.
