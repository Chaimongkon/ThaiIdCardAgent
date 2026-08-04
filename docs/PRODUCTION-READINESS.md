# Production Readiness

Review date: 2026-08-04
Branch: `main`
Scope: Production readiness after real Windows Service Production Acceptance on the test machine.

## 1. Executive Summary

Production Acceptance passed on the test machine for the installed Windows Service running as `NT AUTHORITY\LocalService`. HTTPS, JWT, reader enumeration, card status, ATR, PC/SC access from the service account, CardRemoved, CardInserted, restart, upgrade, uninstall-keep-data, reinstall, and certificate retention were validated.

Current recommendation: **Go for controlled pilot on the validated workstation configuration**. Do not claim full enterprise rollout readiness until SSE events, Windows reboot auto-start, and code signing are completed.

## 2. Build Status

Required build pipeline passed in the latest verification run:

```powershell
dotnet clean -m:1 /nr:false
dotnet restore -m:1 /nr:false
dotnet build -c Release -m:1 /nr:false --no-restore
```

The Release build completed with `0 Warning(s), 0 Error(s)`.

## 3. Test Status

Non-hardware tests passed in the latest verification run:

- Core tests: passed.
- PCSC tests: passed.
- Service tests: passed.
- Hardware test assembly was excluded by `--filter "Category!=Hardware"`.

The latest count is recorded in the final task report.

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
- Replay protection is respected by issuing a fresh JWT for each acceptance API request.
- The acceptance script does not print JWTs.

Private signing keys, JWTs, passwords, and PFX/P12 files must remain outside Git and out of logs.

## 7. API Status Through Windows Service

Validated through the installed service:

- `GET /api/v1/health`: passed.
- `GET /api/v1/readers`: passed.
- `GET /api/v1/card/status`: passed.
- `POST /api/v1/card/atr`: passed.

`POST /api/v1/card/read` remains intentionally not implemented and returns protocol-not-configured behavior until a verified Thai card APDU provider is added.

## 8. Card Transition Status

Validated through status polling:

- CardRemoved: passed after `/api/v1/card/status` returned `NoCard` 2 consecutive times.
- CardInserted: passed after `/api/v1/card/status` returned `CardPresent` 2 consecutive times.

Status polling is not SSE validation.

## 9. SSE Status

Not tested:

- `CardRemoved` over `/api/v1/events`.
- `CardInserted` over `/api/v1/events`.

`scripts\Test-SseEvents.ps1` is available for this check. These must remain reported as `Not Tested` until the script passes against the installed Windows Service and real hardware.

## 10. Installer, Upgrade, And Uninstall Status

Validated on the test machine:

- Install Windows Service: passed.
- Service configuration: passed.
- Start service: passed.
- Restart service health/readers: passed.
- Upgrade: passed.
- Uninstall while preserving config/logs: passed.
- Reinstall: passed.
- Certificate retention: passed.

The installer uses `NT AUTHORITY\LocalService` by default.

## 11. Remaining Risks

- Windows restart and Automatic Delayed Start after reboot: not tested.
- Code signing: executable/installer are unsigned.
- SSE event-stream transitions: not tested.
- Thai card APDU/data provider: not implemented.
- Production rollout must provide managed JWT public verification material and exact allowed origins.

## 12. Go Conditions For Wider Rollout

Before broad rollout:

- Verify SSE `CardRemoved` and `CardInserted` through `/api/v1/events`.
- Verify service starts after Windows restart with Automatic Delayed Start.
- Add Authenticode signing for executable/installer.
- Repeat acceptance on target hardware/driver baselines.
- Keep private keys, JWTs, passwords, and PFX/P12 files out of Git and logs.