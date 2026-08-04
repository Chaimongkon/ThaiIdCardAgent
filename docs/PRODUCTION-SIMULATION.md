# Phase 8/9/10 Production Simulation And Acceptance

Date: 2026-08-04
Repository: `D:\1.FrontEnd Framework\ThaiIdCardAgent`
Branch: `main`
Scope: controlled Production simulation, TLS root-cause validation, Windows Service acceptance, SSE acceptance, reboot validation, and Phase 10 web integration implementation.

## Result

Recommendation: **Go for controlled pilot on the tested workstation configuration after Phase 10 browser manual acceptance passes**.

Production Acceptance was run on a real Windows test machine as Administrator. The installed Windows Service `ThaiIdCardAgent` ran under `NT AUTHORITY\LocalService` and successfully served HTTPS, JWT-authenticated APIs, PC/SC reader detection, card status, ATR, card removal/insertion validation through status polling, and SSE card-change events.

SSE disconnect/reconnect, Windows reboot, Automatic Delayed Start, upgrade, uninstall preserving data, reinstall, and certificate retention have also passed. Code signing remains incomplete.

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
- Windows reboot: passed.
- Automatic Delayed Start: passed.
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
- SSE `/api/v1/events`: passed for CardRemoved and CardInserted.

No JWT, private key, password, PFX/P12, or signing secret is documented here or committed to the repository.

## PC/SC And Card Transitions

Validated under `NT AUTHORITY\LocalService` through the service API:

- PC/SC reader access from the Windows Service account: passed.
- Card status while card removed: `connected=True`, `cardPresent=False`, no ATR.
- Card status after reinsertion: `connected=True`, `cardPresent=True`, ATR present.
- CardRemoved transition: passed by polling `/api/v1/card/status` until `NoCard` was observed 2 consecutive times.
- CardInserted transition: passed by polling `/api/v1/card/status` until `CardPresent` was observed 2 consecutive times.

The status endpoint reads the current PC/SC reader state on each request.

## SSE Status

Validated separately from status polling:

- `CardRemoved` over `GET /api/v1/events`: passed.
- `CardInserted` over `GET /api/v1/events`: passed.
- Client disconnect cleanup: passed.
- Reconnect repeated rounds: passed.

## Phase 10 Web Integration Status

Implemented:

- Runnable Next.js example in `examples/nextjs-client`.
- Server-side token broker that signs short-lived JWTs and keeps private key server-side.
- Browser typed client for Agent APIs.
- Fetch-streaming SSE client with Authorization header, fresh JWT per reconnect, schema validation, and disconnect cleanup.
- UI for health, readers, card state, ATR, and latest event.
- Documentation for web integration, pilot deployment, and security boundaries.

Pending before Phase 10 commit:

- Manual browser acceptance against the installed Windows Service and real hardware.
- Verify no JWT in URL, local/session storage, console logs, or server logs.
- Verify private signing key is not present in browser bundle.

## Build, Test, Publish

Required verification command set:

```powershell
dotnet clean -m:1 /nr:false
dotnet restore -m:1 /nr:false
dotnet build -c Release -m:1 /nr:false --no-restore
dotnet test -c Release -m:1 /nr:false --no-build --filter "Category!=Hardware"
.\scripts\Publish-WinX64.ps1
```

The latest Phase 10 command results are recorded in the final task report.

## Remaining Not Tested Or Incomplete

- Phase 10 browser manual acceptance of `examples/nextjs-client`.
- Authenticode/code signing for executable or installer.
- Thai ID APDU/data reading. No Citizen ID, name, address, birth date, or photo has been read.

## Rollout Notes

Use this acceptance result only for the tested workstation class and configuration. Repeat acceptance on each target image or deployment baseline, especially where PC/SC driver, certificate trust, Windows service policy, browser policy, or endpoint security differs.
