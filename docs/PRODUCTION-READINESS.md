# Production Readiness

Review date: 2026-08-04
Branch: `main`
Scope: Production readiness after real Windows Service Production Acceptance, SSE acceptance, reboot validation, and Phase 10 web integration implementation.

## 1. Executive Summary

Production Acceptance passed on the test machine for the installed Windows Service running as `NT AUTHORITY\LocalService`. HTTPS, JWT, reader enumeration, card status, ATR, PC/SC access from the service account, CardRemoved, CardInserted, SSE events, disconnect/reconnect, Windows reboot, Automatic Delayed Start, upgrade, uninstall-keep-data, reinstall, and certificate retention were validated.

Current recommendation: **Go for controlled pilot on the validated workstation configuration** after the Phase 10 browser example is manually accepted against the installed service and real hardware. Do not claim broad enterprise rollout readiness until code signing and target-environment repeat acceptance are complete.

## 2. Build Status

Required .NET build pipeline from the previous verification run passed:

```powershell
dotnet clean -m:1 /nr:false
dotnet restore -m:1 /nr:false
dotnet build -c Release -m:1 /nr:false --no-restore
```

The Release build completed with `0 Warning(s), 0 Error(s)` in that run. Phase 10 final verification must rerun the full command set before commit.

## 3. Test Status

Non-hardware tests passed in the previous verification run with 87/87 tests. Phase 10 added a Next.js example test suite that passed locally with 15/15 tests.

Hardware test assembly remains excluded from the normal `Category!=Hardware` command. Real service/hardware acceptance is recorded separately below.

## 4. Hardware And PC/SC Status

Validated with real hardware:

- Reader detection: passed.
- Card absent state: passed.
- Card present state: passed.
- ATR through Console and Service API: passed.
- PC/SC access under Windows Service account `NT AUTHORITY\LocalService`: passed.

No personal cardholder data has been read or documented.

## 5. HTTPS Status

Production HTTPS is loopback-only on `https://localhost:18443`.

Validated:

- `GET /api/v1/health`: HTTP 200 through installed service.
- Certificate validation: no bypass used.
- Server mTLS: not required; `ClientCertificateMode.NoCertificate`.
- Root cause of previous TLS failure: machine-level certificate trust mismatch between `CurrentUser\Root` and `LocalMachine\Root`.

## 6. JWT Status

Validated:

- JWT key preflight: passed.
- Short-lived JWT runtime issue: passed.
- Replay protection is respected by issuing a fresh JWT for each acceptance API request and SSE reconnect.
- Acceptance tooling and Next.js example do not print JWTs.

Private signing keys, JWTs, passwords, and PFX/P12 files must remain outside Git and out of logs.

## 7. API Status Through Windows Service

Validated through the installed service:

- `GET /api/v1/health`: passed.
- `GET /api/v1/readers`: passed.
- `GET /api/v1/card/status`: passed.
- `POST /api/v1/card/atr`: passed.
- `GET /api/v1/events`: passed for CardRemoved and CardInserted.

`POST /api/v1/card/read` remains intentionally not implemented and returns HTTP 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED` until a verified Thai card APDU provider is added.

## 8. Card Transition Status

Validated through status polling:

- CardRemoved: passed after `/api/v1/card/status` returned `NoCard` 2 consecutive times.
- CardInserted: passed after `/api/v1/card/status` returned `CardPresent` 2 consecutive times.

Validated through SSE:

- CardRemoved: passed through `/api/v1/events`.
- CardInserted: passed through `/api/v1/events`.
- SSE disconnect cleanup: passed.
- SSE reconnect repeated rounds: passed.

Status polling and SSE remain separate acceptance paths.

## 9. Installer, Upgrade, Reboot, And Uninstall Status

Validated on the test machine:

- Install Windows Service: passed.
- Service configuration: passed.
- Start service: passed.
- Restart service health/readers: passed.
- Windows reboot: passed.
- Automatic Delayed Start: passed.
- Upgrade: passed.
- Uninstall while preserving config/logs: passed.
- Reinstall: passed.
- Certificate retention: passed.

The installer uses `NT AUTHORITY\LocalService` by default.

## 10. Web Integration Status

Implemented in `examples/nextjs-client`:

- Server-side token broker endpoint `POST /api/local-agent/token`.
- Browser typed client for health, readers, card status, ATR, and SSE.
- Fetch-streaming SSE with Authorization header, bounded reconnect, fresh JWT per connection, and disconnect cleanup.
- UI for Agent connectivity, reader/card state, ATR, and latest event.
- Card Read button disabled because Thai card protocol is not configured.
- Security headers and token response `Cache-Control: no-store`.

Automated Next.js lint, typecheck, tests, and production build passed locally. Manual browser acceptance against the installed Windows Service and real hardware is still required before committing Phase 10 as pilot-ready.

## 11. Remaining Risks

- Authenticode/code signing for executable or installer is not implemented.
- Thai card APDU/data provider is not implemented.
- Phase 10 web browser manual acceptance is pending.
- Production rollout must provide managed JWT verification material and exact allowed origins.
- Repeat acceptance is required on target hardware/driver baselines and workstation images.

## 12. Go Conditions For Wider Rollout

Before broad rollout:

- Complete browser pilot acceptance for the web integration.
- Add Authenticode signing for executable/installer.
- Repeat service, PC/SC, HTTPS, JWT, API, SSE, reboot, and upgrade acceptance on target environments.
- Keep private keys, JWTs, passwords, PFX/P12 files, and PII out of Git and logs.

## 13. Release Packaging And Signing Readiness

Pilot release packaging and code-signing readiness are implemented (Phase 11):

- Reproducible, versioned release packages with a SHA-256 checksum manifest and a
  `release-manifest.json` (product, version, git commit, UTC build time, target runtime,
  signing status, file hashes) — see [RELEASE-PROCESS.md](RELEASE-PROCESS.md).
- A signing pipeline that works today with a real code signing certificate (store thumbprint
  or PFX + SecureString), enforces the Code Signing EKU, rejects expired/not-yet-valid and
  HTTPS certificates, and fails when timestamping fails — see [CODE-SIGNING.md](CODE-SIGNING.md).
- Explicit **UnsignedPilot** mode for controlled pilots, with the checksum manifest as the
  out-of-band integrity check.
- Install-time integrity verification (checksum, optional `-RequireSigned`) and
  rollback-protected upgrades that preserve config/logs.
- Automated coverage in `tests/ThaiIdCardAgent.Release.Tests` (checksum correctness, tamper
  rejection, missing/malformed manifest, secret exclusion, deterministic ordering, unsigned
  pilot, RequireSigned rejection, EKU/expiry validation, signature-failure and timestamp-failure
  handling, PFX-password non-logging, rollback, and PowerShell 5.1 parsing).

**Still required for real production signing:** a code signing certificate with the Code
Signing EKU, issued by a CA trusted on target machines, plus a reachable RFC 3161 timestamp
server. Pilot binaries remain unsigned until then.

## 14. Clean-Machine Pilot Acceptance (Phase 12)

Clean-machine acceptance tooling is implemented so a pilot can be deployed and verified from a
release ZIP alone (no source tree):

- [`scripts/Test-PilotDeployment.ps1`](../scripts/Test-PilotDeployment.ps1) — modes VerifyOnly /
  Tamper / Rollback (no Administrator or hardware needed) and Full (installs the service and
  exercises the APIs). Hardware steps are interactive and skippable; a skipped step is reported
  **Not Tested**, never Passed. A failure is never reported as Passed. The source ZIP is never
  modified.
- [`scripts/Get-AgentDiagnostics.ps1`](../scripts/Get-AgentDiagnostics.ps1) — read-only,
  sanitized diagnostics with JSON export (no JWT/private key/password/Authorization/PII).
- [PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md) — the machine checklist and
  sign-off.
- Automated coverage in `tests/ThaiIdCardAgent.Release.Tests` (valid package, missing ZIP,
  malformed manifest, checksum mismatch, secret found, UnsignedPilot accept/reject, WhatIf no
  side effects, tamper rejection + original ZIP unmodified, rollback, config/log retention,
  diagnostics-no-secret, PowerShell 5.1 parsing).

**Status:** the automated modes pass without hardware. **Clean-machine acceptance on a real
pilot machine (install, HTTPS/service/reader, hardware transitions, reboot, upgrade, rollback)
is operator-run and remains PENDING** until executed and signed off. This is **not** a claim of
full production readiness.
