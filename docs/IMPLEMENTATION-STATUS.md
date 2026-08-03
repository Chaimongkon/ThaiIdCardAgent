# Implementation Status

## Implemented

- .NET 10 solution and project structure.
- Core models, interfaces, result model, exceptions, error mapping, and PII redaction.
- Windows PC/SC reader listing, card presence, ATR, and polling monitor.
- Bitwise PC/SC state mapping for combined flags.
- Minimal API with authenticated local smart card endpoints.
- Development key authentication from configuration/user secrets/environment.
- Production JWT validation with issuer, audience, lifetime, required claims, and replay detection.
- Exact-origin CORS.
- Windows Service hosting configuration.
- Publish/install/uninstall/certificate scripts with `-WhatIf` support where applicable.
- Next.js TypeScript client and example component.
- Production `--diagnostics` command that checks configuration without opening a listener.

## Tested Without Hardware

- Standard `dotnet clean`, `dotnet restore`, and `dotnet build -c Release` passed outside the Codex sandbox.
- Non-hardware tests passed: Core 3, PCSC 18, Service 24.
- Integration coverage for health, auth failures, Development key, JWT failures, replay token, CORS, reader selection, missing reader, no card, ATR success, agent busy, protocol-not-configured, requestId, and production error redaction.
- win-x64 publish produced `artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe`.
- Published executable diagnostics ran without opening a listener.
- Install/uninstall/certificate scripts were checked with `-WhatIf`; Windows Service was not installed in the Phase 7 run because the process was not Administrator.

## Tested With Hardware

Test date: 2026-08-03

- Reader: Identive SCR33xx v2.0 USB SC Reader 0
- Reader detection: passed.
- Card presence: passed.
- ATR: passed.
- CardInserted: previously passed with real hardware transition.
- CardRemoved: previously passed with real hardware transition.
- Hardware test: passed 1 test with `THAI_ID_AGENT_HARDWARE_TESTS=1`.
- ATR used for verification: `3B-79-96-00-00-54-48-20-4E-49-44-20-31-33`.

Hardware API verified through the local API on `http://127.0.0.1:18442`:

- `GET /api/v1/readers`: returned `isConnected=true`, `isCardPresent=true`, and the ATR above.
- `GET /api/v1/card/status`: returned `CardPresent` and the ATR above.
- `POST /api/v1/card/atr`: returned the ATR above.

## Phase 7 Production Readiness

Tested in Console:

- Final Phase 7 tests passed 46/46.
- Development `--diagnostics` passed with one warning for missing JWT public key, which is expected in Development key mode.
- Production `--diagnostics` found SCardSvr, one PC/SC reader, free port 18443, and a trusted loopback certificate.

Tested over HTTP Development:

- Local API hardware endpoints previously passed on `http://127.0.0.1:18442`.

Tested over HTTPS Production:

- Certificate discovery diagnostics passed.
- Published executable listened on `https://localhost:18443`, but TLS handshake failed from PowerShell and `curl.exe` without bypassing certificate validation. HTTPS is not marked passed.
- Full HTTPS service health from an installed Windows Service has not been tested in this session.

Tested as Windows Service:

- Not tested in this session. Current process is not Administrator.
- Dry-run scripts are available with `-WhatIf`.

Blocked by External Dependency:

- Production `Agent:AllowedOrigins` is not configured in this environment.
- Production JWT public verification key is not configured in this environment.
- Real install/upgrade/uninstall requires Administrator approval.
- Service-account PC/SC access requires an installed Windows Service.

## Not Implemented Or Blocked

- Citizen ID reading.
- Cardholder name reading.
- Birth date reading.
- Address reading.
- Photo reading.
- Thai Card APDU provider.
- Real Central Member API integration.

No Citizen ID, owner name, address, birth date, or photo has been read or documented.

## Security Limitations

- Production deployments must configure public JWT verification material or authority configuration; private signing keys must not be stored in the agent.
- Development key authentication is disabled outside Development environment.
- The local API is designed for loopback binding only.
