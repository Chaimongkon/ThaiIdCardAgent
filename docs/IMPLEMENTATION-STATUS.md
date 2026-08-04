# Implementation Status

## Implemented

- .NET 10 solution and project structure.
- Core models, interfaces, result model, exceptions, error mapping, and PII redaction.
- Windows PC/SC reader listing, card presence, ATR, and polling monitor.
- Bitwise PC/SC state mapping for combined flags.
- Minimal API with authenticated local smart card endpoints.
- Development key authentication from configuration/user secrets/environment.
- Production JWT validation with issuer, audience, lifetime, required claims, and replay detection.
- Acceptance tooling that issues a fresh JWT for every Production API request.
- Authenticated SSE `/api/v1/events` with disconnect cleanup.
- Exact-origin CORS.
- Windows Service hosting configuration.
- Publish/install/uninstall/certificate scripts with `-WhatIf` support where applicable.
- Production `--diagnostics` command that checks configuration without opening a listener.
- Runnable Next.js integration example with server-side JWT broker, typed Agent client, fetch-streaming SSE, and UI tests.

## Tested Without Hardware

- `dotnet clean -m:1 /nr:false`: passed.
- `dotnet restore -m:1 /nr:false`: passed.
- `dotnet build -c Release -m:1 /nr:false --no-restore`: passed with `0 Warning(s), 0 Error(s)` in the previous verification run.
- `dotnet test -c Release -m:1 /nr:false --no-build --filter "Category!=Hardware"`: passed with 87/87 in the previous verification run.
- win-x64 publish produced `artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe` in the previous verification run.
- PowerShell scripts parse under Windows PowerShell 5.1.
- Next.js example: lint, typecheck, unit tests, and production build passed locally after Phase 10 implementation.

## Tested With Hardware

Validated with a real PC/SC reader/card on the test machine:

- Reader detection: passed.
- Card absent state: passed.
- Card present state: passed.
- ATR: passed.
- Console status while card removed: `connected=True`, `cardPresent=False`, no ATR.
- Console status after reinsertion: `connected=True`, `cardPresent=True`, ATR present.

No Citizen ID, owner name, address, birth date, or photo has been read or documented.

## Production Acceptance Through Windows Service

Production Acceptance passed on the test machine:

- Windows Service installed and running.
- Service account: `NT AUTHORITY\LocalService`.
- PC/SC under service account: passed.
- HTTPS health without certificate-validation bypass: passed.
- JWT key preflight and runtime issue: passed.
- Readers API through service: passed.
- Card status API through service: passed.
- Card ATR API through service: passed.
- CardRemoved through status polling: passed after `NoCard` appeared 2 consecutive times.
- CardInserted through status polling: passed after `CardPresent` appeared 2 consecutive times.
- SSE CardRemoved through `/api/v1/events`: passed under Windows Service with real hardware.
- SSE CardInserted through `/api/v1/events`: passed under Windows Service with real hardware.
- SSE disconnect and reconnect repeated rounds: passed.
- Restart service health/readers: passed.
- Windows reboot and Automatic Delayed Start: passed.
- Upgrade: passed.
- Uninstall preserving config/logs: passed.
- Reinstall: passed.
- Certificate retention: passed.

## Not Tested

- Phase 10 browser manual acceptance of the new `examples/nextjs-client` UI against the installed Windows Service and real hardware.
- Code signing validation because executable/installer are still unsigned.

## Not Implemented

- Citizen ID reading.
- Cardholder name reading.
- Birth date reading.
- Address reading.
- Photo reading.
- Thai Card APDU provider.
- Real Central Member API integration.
- Authenticode signing of executable/installer.

## Security Limitations

- Production deployments must configure public JWT verification material and exact allowed origins.
- Private signing keys, JWTs, passwords, PFX/P12 files, and machine-specific secrets must not be stored in Git or logs.
- Development key authentication is disabled outside Development environment.
- The local API is designed for loopback binding only.
- Executable/installer signing remains incomplete.
