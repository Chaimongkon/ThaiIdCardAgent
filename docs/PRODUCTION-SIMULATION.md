# Phase 8/9 Production Simulation And Acceptance

Date: 2026-08-04
Repository: `D:\1.FrontEnd Framework\ThaiIdCardAgent`
Branch: `main`
Scope: controlled Production simulation, TLS root-cause validation, Windows Service acceptance, and service-account PC/SC behavior.

## Result

Recommendation: **Go for controlled pilot on the tested workstation configuration only**.

Production Acceptance was run on a real Windows test machine as Administrator. The installed Windows Service `ThaiIdCardAgent` ran under `NT AUTHORITY\LocalService` and successfully served HTTPS, JWT-authenticated APIs, PC/SC reader detection, card status, ATR, and card removal/insertion validation through status polling.

This does not complete every production-readiness item. SSE card-change events, Windows restart/Automatic Delayed Start after reboot, and code signing remain not tested or incomplete.

## TLS Root Cause And Resolution

Root cause proven during Phase 9:

- The HTTPS server certificate was installed in `Cert:\LocalMachine\My`.
- The public certificate was trusted only in `CurrentUser\Root` at first.
- `LocalMachine\Root` did not contain the public certificate, so machine-context Schannel validation failed.
- After importing the public certificate into `Cert:\LocalMachine\Root`, `certutil`, `curl.exe`, and `Invoke-WebRequest` succeeded without certificate-validation bypass.

Current validated behavior:

- Production HTTPS binds loopback on `https://localhost:18443`.
- `GET /api/v1/health` returns HTTP 200 through the installed service.
- No `-k`, `--insecure`, custom callback, or certificate-validation bypass was used.
- Server TLS uses `ClientCertificateMode.NoCertificate`; mTLS is not required.

## Windows Service Acceptance

Validated on the test machine:

- Service name: `ThaiIdCardAgent`.
- Service account: `NT AUTHORITY\LocalService`.
- Service status: Running during acceptance.
- Install: passed.
- Service configuration: passed.
- Start service: passed.
- Restart service and health/readers recheck: passed.
- Upgrade: passed.
- Uninstall while keeping config/logs: passed.
- Reinstall: passed.
- Certificate retention: passed. Scripts did not delete certificates.

## JWT And HTTPS APIs

Validated through the installed Windows Service:

- JWT key preflight: passed.
- JWT runtime issue: passed.
- HTTPS health: passed without certificate-validation bypass.
- Readers API: passed.
- Card status API: passed.
- Card ATR API: passed.

No JWT, private key, password, PFX/P12, or signing secret is documented here or committed to the repository.

## PC/SC And Card Transitions

Validated under `NT AUTHORITY\LocalService` through the service API:

- PC/SC reader access from the Windows Service account: passed.
- Card status while card removed: `connected=True`, `cardPresent=False`, no ATR.
- Card status after reinsertion: `connected=True`, `cardPresent=True`, ATR present.
- CardRemoved transition: passed by polling `/api/v1/card/status` until `NoCard` was observed 2 consecutive times.
- CardInserted transition: passed by polling `/api/v1/card/status` until `CardPresent` was observed 2 consecutive times.

The status endpoint reads the current PC/SC reader state on each request. Status polling success does not prove SSE event delivery.

## SSE Status

Not tested:

- `CardRemoved` over `GET /api/v1/events`.
- `CardInserted` over `GET /api/v1/events`.

SSE must be tested separately from status polling before claiming event-stream acceptance. Use `scripts\Test-SseEvents.ps1` against the installed Windows Service and real hardware.

## Build, Test, Publish

Required verification command set:

```powershell
dotnet clean -m:1 /nr:false
dotnet restore -m:1 /nr:false
dotnet build -c Release -m:1 /nr:false --no-restore
dotnet test -c Release -m:1 /nr:false --no-build --filter "Category!=Hardware"
.\scripts\Publish-WinX64.ps1
```

The latest run for this documentation update is recorded in the final task report.

## Remaining Not Tested Or Incomplete

- SSE `CardRemoved` through `/api/v1/events`.
- SSE `CardInserted` through `/api/v1/events`.
- Windows restart and Automatic Delayed Start after reboot.
- Authenticode/code signing for executable or installer.
- Thai ID APDU/data reading. No Citizen ID, name, address, birth date, or photo has been read.

## Rollout Notes

Use this acceptance result only for the tested workstation class and configuration. Repeat acceptance on each target image or deployment baseline, especially where PC/SC driver, certificate trust, Windows service policy, or endpoint security differs.