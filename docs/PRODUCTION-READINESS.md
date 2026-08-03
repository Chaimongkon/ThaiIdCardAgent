# Production Readiness

Review date: 2026-08-03
Branch: `main`
Scope: Phase 7 production readiness review for ThaiIdCardAgent. Phase 8 simulation details are in `docs/PRODUCTION-SIMULATION.md`.

## 1. Executive Summary

The repository builds and tests successfully on the host when commands are run outside the Codex managed sandbox. The smart card reader, card presence, ATR, and local Development API were previously verified with real hardware. Phase 7 added a non-listening `--diagnostics` command and hardened the install script for upgrade/reinstall behavior.

Current recommendation: **No-Go for unattended Production rollout** until Production configuration is supplied and Windows Service installation is tested from an elevated administrator session.

## 2. Build Status

Tested outside the Codex sandbox:

- `dotnet clean`: passed.
- `dotnet restore`: passed.
- `dotnet build -c Release`: passed with `0 Warning(s), 0 Error(s)`.

Inside the Codex sandbox, default parallel solution graph commands can fail with no logged errors. See `docs/BUILD-TROUBLESHOOTING.md`.

## 3. Test Status

Final Phase 7 verification passed:

- Core tests: 3 passed.
- PCSC tests: 18 passed.
- Service tests: 24 passed.
- Hardware tests: 1 passed with `THAI_ID_AGENT_HARDWARE_TESTS=1`.
- Total: 46 passed, 0 failed, 0 skipped.

## 4. Hardware Status

Tested in Console with real hardware on 2026-08-03:

- Reader detection: passed.
- Card presence: passed.
- ATR: passed.
- CardInserted/CardRemoved: previously passed with real card transition.

No personal cardholder data has been read.

## 5. Local API Status

Tested over HTTP Development on `http://127.0.0.1:18442`:

- `GET /api/v1/health`: passed.
- `GET /api/v1/readers`: passed with authentication.
- `GET /api/v1/card/status`: passed with authentication.
- `POST /api/v1/card/atr`: passed with authentication.
- `POST /api/v1/card/read`: returns `501 THAI_CARD_PROTOCOL_NOT_CONFIGURED`.

## 6. Windows Service Status

Not installed in this Phase 7 run because the current process is not Administrator. Dry run is supported through:

```powershell
.\scripts\Install-Service.ps1 -WhatIf
```

The install script now supports upgrade/reinstall by stopping an existing service before copy, preserving config/log data, and updating service configuration.

## 7. HTTPS Status

Production HTTPS binding is loopback-only on `https://localhost:18443`; the certificate must match the host name used by clients.

Production diagnostics found an HTTPS certificate in the LocalMachine store with:

- trusted chain: passed.
- private key visible to current process: passed.
- SAN matches localhost: passed for the current certificate. IP address `127.0.0.1` is not covered unless an IP SAN is added.

Console-mode Production HTTPS was attempted from the published executable. The process listened on `https://localhost:18443`, and HTTP `18442` was not available, but PowerShell and `curl.exe` failed the TLS handshake without bypassing certificate validation. `curl.exe` reported `SEC_E_NO_CREDENTIALS (0x8009030e)`. This is not marked as passed.

## 8. Certificate Status

Configured lookup defaults:

- Store: `LocalMachine\My`.
- SubjectName: `localhost`.

No certificate password, private key PEM, or generated code-signing certificate is stored in the repository.

## 9. JWT Status

Production diagnostics currently fail because no JWT public verification key is configured in this environment.

Previously tested in service integration tests:

- replay token rejected.
- expired token rejected.
- wrong audience rejected.
- missing `jti`, `sub`, or `workstation_id` rejected.

The agent must receive public verification material only. Private signing keys must stay outside the agent.

## 10. CORS Status

Development CORS origin is configured as `http://localhost:3000`.

Production diagnostics currently fail because `Agent:AllowedOrigins` is empty. Production must configure exact origins only. Wildcards are rejected by options validation and tests.

## 11. Installer Status

Publish/install/uninstall scripts parse successfully. Install real execution is blocked by missing Administrator privileges in this session.

The installer:

- installs to `C:\Program Files\ThaiIdCardAgent`.
- stores config/logs under `C:\ProgramData\ThaiIdCardAgent`.
- grants service account access to ProgramData.
- configures LocalService by default.
- sets delayed automatic startup and recovery actions.

## 12. Upgrade Status

Upgrade logic is implemented but not tested against a real installed service in this session.

Expected behavior:

- stop existing service before copy.
- preserve existing config/logs.
- back up existing config when present.
- update service binary path, display name, startup, account, description, and recovery actions.
- health check after start unless `-SkipStart` is used.

## 13. Uninstall Status

Uninstall script removes the program folder and keeps ProgramData by default. `-RemoveData` deletes ProgramData only when explicitly requested. Certificates are not deleted automatically.

Real uninstall was not run in this session.

## 14. Service Account Status

Default service account: `NT AUTHORITY\LocalService`.

Service-account PC/SC access was not tested because the service was not installed. Console and diagnostics PC/SC checks ran under the current user account only.

## 15. Known Risks

- Production `Agent:AllowedOrigins` must be supplied by deployment.
- Production JWT public key or authority configuration must be supplied by deployment.
- LocalService access to the smart card stack must be verified on the target workstation.
- HTTPS TLS handshake currently fails in console-mode Production test and must be resolved before rollout.
- HTTPS must also be tested from the installed Windows Service after the console-mode TLS issue is resolved.
- Code signing is not implemented; `ThaiIdCardAgent.Service.exe` is currently unsigned.
- Thai card APDU/data provider is not implemented.

## 16. Blocked Items

- Real Windows Service install: blocked by non-Administrator process.
- Service account hardware test: blocked until service is installed.
- Restart Windows auto-start test: blocked until service is installed.
- Installer upgrade/uninstall real tests: blocked until elevated service install is allowed.

## 17. Go/No-Go Recommendation

No-Go for Production rollout today.

Go conditions:

- Configure exact Production `Agent:AllowedOrigins`.
- Configure JWT public verification material outside Git.
- Install from an elevated administrator session.
- Resolve the current TLS handshake failure, then verify HTTPS `GET /api/v1/health` over `https://localhost:18443` or a SAN-matching host without bypassing certificate validation.
- Verify `/readers`, `/card/status`, and `/card/atr` through the installed service account with a real reader/card.
- Verify install, upgrade, uninstall, and Windows restart behavior.
