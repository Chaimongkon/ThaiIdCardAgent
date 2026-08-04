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
- Exact-origin CORS.
- Windows Service hosting configuration.
- Publish/install/uninstall/certificate scripts with `-WhatIf` support where applicable.
- Next.js TypeScript client and example component.
- Production `--diagnostics` command that checks configuration without opening a listener.

## Tested Without Hardware

- `dotnet clean -m:1 /nr:false`: passed.
- `dotnet restore -m:1 /nr:false`: passed.
- `dotnet build -c Release -m:1 /nr:false --no-restore`: passed with `0 Warning(s), 0 Error(s)`.
- `dotnet test -c Release -m:1 /nr:false --no-build --filter "Category!=Hardware"`: passed.
- win-x64 publish produced `artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe`.
- PowerShell scripts parse under Windows PowerShell 5.1.

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
- Restart service health/readers: passed.
- Upgrade: passed.
- Uninstall preserving config/logs: passed.
- Reinstall: passed.
- Certificate retention: passed.

## Not Tested

- SSE `CardRemoved` through `/api/v1/events`.
- SSE `CardInserted` through `/api/v1/events`.
- Windows restart and Automatic Delayed Start after reboot.
- Code signing of executable/installer.

## Not Implemented

- Citizen ID reading.
- Cardholder name reading.
- Birth date reading.
- Address reading.
- Photo reading.
- Thai Card APDU provider.
- Real Central Member API integration.

## Security Limitations

- Production deployments must configure public JWT verification material or authority configuration.
- Private signing keys, JWTs, passwords, PFX/P12 files, and machine-specific secrets must not be stored in Git or logs.
- Development key authentication is disabled outside Development environment.
- The local API is designed for loopback binding only.
- Executable/installer signing remains incomplete.