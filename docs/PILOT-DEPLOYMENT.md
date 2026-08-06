# Pilot Deployment

Pilot scope is limited to the tested workstation class and configuration until broader rollout evidence is collected.

## Confirmed On Test Machine

- Windows Service `ThaiIdCardAgent` installed and running.
- Service account: `NT AUTHORITY\LocalService`.
- PC/SC access under LocalService: passed.
- HTTPS health on `https://localhost:18443`: passed without certificate-validation bypass.
- JWT authentication and replay-safe fresh-token flow: passed.
- Readers API: passed.
- Card status API: passed.
- Card ATR API: passed.
- CardRemoved and CardInserted via status polling: passed.
- SSE CardRemoved and CardInserted via `/api/v1/events`: passed under Windows Service with real hardware.
- SSE disconnect and reconnect repeated rounds: passed.
- Windows reboot and Automatic Delayed Start: passed.
- Install, upgrade, uninstall preserving data, reinstall, and certificate retention: passed.

## Pilot Checklist

- Use a managed server-side JWT signing key; keep private key out of browser bundles and Git.
- Configure exact Agent CORS origins for the pilot web origin.
- Install certificate trust in the correct machine scope.
- Install the service as `NT AUTHORITY\LocalService` unless a formal security decision requires another account.
- Verify `GET /api/v1/health` after reboot.
- Verify readers/status/ATR through the service account on each hardware baseline.
- Verify SSE CardRemoved/CardInserted separately from status polling.
- Capture only non-PII diagnostics: service status, endpoint status codes, reader names, event types, timestamps, and ATR hex.

## Still Incomplete

- Executable/installer code signing is not implemented; published binaries are unsigned.
- Thai card APDU/data reading is not implemented and `/api/v1/card/read` returns `THAI_CARD_PROTOCOL_NOT_CONFIGURED`.
- Wider enterprise rollout has not been validated on every target workstation image, driver version, endpoint security policy, or reader/card combination.

## Web Example Manual Acceptance

Before committing the Phase 10 web integration as pilot-ready, run the Next.js example against the installed service and real hardware:

1. Start `examples/nextjs-client` with `.env.local` pointing to a server-side test private key.
2. Open the browser app.
3. Check Agent.
4. Refresh Readers and verify reader name/count.
5. Remove card and verify SSE `CardRemoved`; verify status shows `NoCard`.
6. Insert card and verify SSE `CardInserted`; verify status shows `CardPresent`.
7. Read ATR and verify uppercase hex only.
8. Connect/disconnect SSE at least 3 rounds.
9. Confirm JWT is not in URL, `localStorage`, `sessionStorage`, console output, or logs.
10. Confirm private key text is not present in the production browser bundle.

## Release packaging and integrity (multi-machine pilot)

For rollout to more than one machine, distribute a versioned release package rather than a raw
publish folder, so each machine can verify integrity independently.

1. Build the package (see [RELEASE-PROCESS.md](RELEASE-PROCESS.md)):

   ```powershell
   .\scripts\New-ReleasePackage.ps1 -Version '0.1.0-pilot'
   ```

   Produces `artifacts/release/ThaiIdCardAgent-0.1.0-pilot-win-x64/` with `app/`,
   `checksums.sha256`, `release-manifest.json`, and a deterministic zip.

2. Pilot builds are **UnsignedPilot**. Either accept unsigned explicitly
   (`Sign-Release.ps1 -Unsigned`, loud warning) or sign with a real code signing certificate
   for production (see [CODE-SIGNING.md](CODE-SIGNING.md)). Unsigned builds trigger SmartScreen
   / unknown-publisher warnings and must only go to controlled pilot machines.

3. On each target machine, verify before installing:

   ```powershell
   .\scripts\Test-ReleaseSignature.ps1 -PackagePath <package>            # integrity (pilot)
   .\scripts\Test-ReleaseSignature.ps1 -PackagePath <package> -RequireSigned   # production
   ```

4. Install with integrity enforcement and rollback protection:

   ```powershell
   .\scripts\Install-Service.ps1 -PackagePath <package> [-RequireSigned]
   ```

### Clean-machine acceptance (from the release ZIP)

On a fresh Windows machine or VM with no source tree, drive acceptance from the ZIP with
[`scripts/Test-PilotDeployment.ps1`](../scripts/Test-PilotDeployment.ps1) and record results in
[PILOT-ACCEPTANCE-CHECKLIST.md](PILOT-ACCEPTANCE-CHECKLIST.md):

```powershell
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode VerifyOnly
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode Tamper
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode Rollback
.\scripts\Test-PilotDeployment.ps1 -ReleaseZipPath <release.zip> -Mode Full `
    -CertificateThumbprint <thumb> -CertificateHostName localhost `
    -JwtPublicKeyPath <public.pem> -JwtPrivateKeyPath <acceptance-only.pem> -AllowedOrigin https://localhost:3000
.\scripts\Get-AgentDiagnostics.ps1 -AsJson > agent-diagnostics.json   # sanitized; safe to attach
```

Verify/Tamper/Rollback run without Administrator or hardware; Full installs the service and
exercises the APIs, with interactive, skippable hardware steps (skipped = Not Tested). Clean-machine
acceptance on a real pilot machine is operator-run and remains pending until executed.

### Unsigned pilot acceptance checklist

- Package built and `checksums.sha256` verifies (`Test-ReleaseSignature.ps1`).
- Deliberately modifying one file causes verification to **fail** (tamper is detected).
- `release-manifest.json` shows `signingStatus = UnsignedPilot`, the git commit, and build time.
- Package contains **no** secrets (no PFX/private key/`.env.local`/JWT/logs).
- Operators are informed of the SmartScreen/unknown-publisher warning and the out-of-band
  checksum verification step.
- Upgrade rollback verified: a failed copy restores the previous working install; config/logs
  are preserved.
