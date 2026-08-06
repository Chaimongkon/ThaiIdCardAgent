# Pilot Machine Acceptance Checklist

Use this checklist when deploying a ThaiIdCardAgent **release ZIP** to a clean Windows machine
or VM that has no source tree and has never had the agent installed. It proves the pilot can be
deployed from the release package alone.

Automated portions are driven by [`scripts/Test-PilotDeployment.ps1`](../scripts/Test-PilotDeployment.ps1).
Read-only diagnostics come from [`scripts/Get-AgentDiagnostics.ps1`](../scripts/Get-AgentDiagnostics.ps1).
See [RELEASE-PROCESS.md](RELEASE-PROCESS.md) and [CODE-SIGNING.md](CODE-SIGNING.md) for context.

> Pilot packages are **UnsignedPilot**. SmartScreen / "unknown publisher" warnings are expected
> until a real Code Signing certificate is used. Do not distribute unsigned builds publicly.

## Files to bring to the pilot machine (no source tree required)

Copy these to the pilot machine. Do **not** rely on any other file from the source tree.

| File | Purpose |
| --- | --- |
| `ThaiIdCardAgent-<version>-win-x64.zip` | The release package (verified before install) |
| `scripts/Test-PilotDeployment.ps1` | Acceptance orchestrator |
| `scripts/ReleasePackaging.psm1` | Shared module (imported by the above) |
| `scripts/Install-Service.ps1`, `Uninstall-Service.ps1`, `Set-CertificatePrivateKeyAcl.ps1`, `Test-ReleaseSignature.ps1`, `Test-SseEvents.ps1` | Invoked by Full mode |
| `ThaiIdCardAgent.TestJwt.exe` (published) | JWT minting for Full mode — see below |
| The JWT **public** key PEM (+ an acceptance-only private key PEM for Full mode) | JWT auth |

**Creating the bundle (automated):** Build the standalone clean-machine acceptance bundle with:

```powershell
.\scripts\New-PilotAcceptanceBundle.ps1
```

This publishes `ThaiIdCardAgent.TestJwt.exe` (self-contained win-x64), copies the required scripts and release ZIPs, scans for secret exclusions, and generates a strict `TOOLING-SHA256.txt` manifest.

**Verifying bundle integrity on the clean machine:**

```powershell
Import-Module .\ReleasePackaging.psm1
$verify = Test-ToolingChecksumManifest -BundleRoot .
if ($verify.Ok) {
    Write-Host "Bundle integrity verified: PASSED" -ForegroundColor Green
} else {
    Write-Error "Bundle integrity verification FAILED"
    $verify.Messages
}
```

Record each item as **Passed / Failed / Not Tested**. A skipped hardware step is **Not Tested**,
never Passed. Never record secrets (JWT, private keys, PFX passwords) or cardholder data here.

## A. Machine prerequisites

| # | Item | Result | Notes |
|---|------|--------|-------|
| A1 | Windows version (client/server, build) | | |
| A2 | Administrator access available | | |
| A3 | Smart Card service (`SCardSvr`) running | | |
| A4 | Reader driver installed; reader visible in Device Manager | | |
| A5 | HTTPS certificate present in `LocalMachine\My` and its chain trusted | | thumbprint only |
| A6 | LocalService has read access to the certificate private key | | `Set-CertificatePrivateKeyAcl.ps1` |
| A7 | `Agent__AllowedOrigins__0` set to the exact web origin | | no wildcard |
| A8 | JWT public verification key present on the machine (`Agent__Jwt__PublicKeyPath`) | | public key only |
| A9 | Firewall / endpoint security permits loopback `18443` | | |
| A10 | Supported browser available for the web client | | |

## B. Package integrity (no install) — `-Mode VerifyOnly`

```powershell
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode VerifyOnly
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| B1 | Release ZIP present and readable | | |
| B2 | Package extracts to a temporary directory | | |
| B3 | `release-manifest.json` present and readable | | version / commit recorded |
| B4 | SHA-256 checksum verifies for every payload file | | |
| B5 | No secrets in payload (PFX/key/JWT/`.env.local`/logs/PII) | | |
| B6 | Signing status recorded; UnsignedPilot warning shown | | |

## C. Tamper rejection — `-Mode Tamper`

```powershell
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode Tamper
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| C1 | A modified package copy is rejected before install | | |
| C2 | The original release ZIP is unmodified | | hash unchanged |
| C3 | Any existing install/config is left untouched | | |

## D. Install and service — `-Mode Full` (Administrator)

```powershell
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release-0.1.0.zip> -Mode Full `
    -CertificateThumbprint <THUMBPRINT> -CertificateHostName localhost `
    -JwtPublicKeyPath <public.pem> -JwtPrivateKeyPath <acceptance-only-private.pem> `
    -JwtToolPath <bundle>\ThaiIdCardAgent.TestJwt.exe `
    -UpgradeZipPath <release-0.1.1.zip> `           # different version → real upgrade acceptance
    -AllowedOrigin https://localhost:3000
```

Omit `-UpgradeZipPath` and installing the same package again is only **reinstall/repair**, not a
version upgrade (reported separately).

| # | Item | Result | Notes |
|---|------|--------|-------|
| D1 | Service installs from the package (checksum enforced) | | add `-RequireSigned` once signed |
| D2 | Service account = `NT AUTHORITY\LocalService` | | |
| D3 | Start mode = Automatic | | |
| D4 | Automatic **Delayed** Start enabled | | |
| D5 | Certificate private-key ACL grants LocalService read | | |
| D6 | Service starts | | |
| D7 | HTTPS health OK **without** certificate-validation bypass | | |
| D8 | JWT authentication enforced (unauth rejected, auth accepted) | | |
| D9 | Readers API returns the reader | | |
| D10 | Card Status API works | | |
| D11 | Card ATR API works | | |

## E. Hardware transitions (interactive; skippable → Not Tested)

| # | Item | Result | Notes |
|---|------|--------|-------|
| E1 | CardRemoved via status polling | | |
| E2 | CardInserted via status polling | | |
| E3 | CardRemoved via SSE `/api/v1/events` | | |
| E4 | CardInserted via SSE | | |
| E5 | SSE disconnect / reconnect (repeat ≥3) | | |

## F. Lifecycle

| # | Item | Result | Notes |
|---|------|--------|-------|
| F1 | Restart service; health + readers still OK | | |
| F2 | Reinstall/repair with the **same** package | | not a version upgrade |
| F3 | **Version upgrade** to a different package (0.1.0 → 0.1.1): manifest version changes | | `-UpgradeZipPath` |
| F4 | Config/logs retained across upgrade | | |
| F5 | Service account / start mode unchanged across upgrade | | |
| F6 | Uninstall keeps data (`ProgramData` retained) | | |
| F7 | Reinstall succeeds | | |

### Reboot continuation (explicit two-stage)

Reboot is a real machine event and is **never** reported as Passed by the pre-reboot run. After the
Full run, reboot the machine, then run the post-reboot stage:

```powershell
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode PostReboot `
    -JwtPublicKeyPath <public.pem> -JwtPrivateKeyPath <acceptance-only.pem> -JwtToolPath <bundle>\ThaiIdCardAgent.TestJwt.exe
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| F8 | After reboot: service state = Running | | `PostReboot` |
| F9 | After reboot: start mode = Auto, DelayedAutoStart = 1 | | |
| F10 | After reboot: account = `NT AUTHORITY\LocalService` | | |
| F11 | After reboot: HTTPS health 200 (no cert bypass) | | |
| F12 | After reboot: Readers API works | | |

> A real version upgrade should also bump the assembly/file version so the executable version
> changes; the acceptance verifies the release-manifest version change deterministically.

## G. Rollback — `-Mode Rollback`

```powershell
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode Rollback
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| G1 | Copy failure restores the previous binary | | |
| G2 | Config/logs untouched by rollback | | |
| G3 | Invalid manifest / checksum mismatch rejected before install | | |
| G4 | Service-start-failure rollback (real service; explicit confirmation) | | often Not Tested |

## H. Diagnostics and logs

```powershell
.\scripts\Get-AgentDiagnostics.ps1
.\scripts\Get-AgentDiagnostics.ps1 -AsJson > agent-diagnostics.json   # sanitized; safe to attach
```

| # | Item | Result | Notes |
|---|------|--------|-------|
| H1 | Diagnostics show service state/account/start mode/PID | | |
| H2 | Diagnostics show certificate subject/thumbprint/expiry + key accessibility | | |
| H3 | Diagnostics show Smart Card service state and Agent health | | |
| H4 | Diagnostics output contains **no** JWT/private key/password/Authorization/PII | | |
| H5 | Browser console shows no secret or PII | | |

## I. Sign-off

| Field | Value |
|-------|-------|
| Release version / git commit | |
| Signing status | UnsignedPilot / Signed |
| Machine identifier | |
| Tester name | |
| Date | |
| Overall result (Passed / Failed / Partial) | |
| Outstanding items / Not Tested | |

> Thai card data reading (`/api/v1/card/read`) is **not configured** (`THAI_CARD_PROTOCOL_NOT_CONFIGURED`)
> and is out of scope for this acceptance.
