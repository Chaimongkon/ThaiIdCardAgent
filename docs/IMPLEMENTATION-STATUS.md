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

## Tested Without Hardware

- Release build with SDK 10.0.302 using `-m:1` on this machine.
- Non-hardware tests: Core 3, PCSC 18, Service 24.
- Integration coverage for health, auth failures, Development key, JWT failures, replay token, CORS, reader selection, missing reader, no card, ATR success, agent busy, protocol-not-configured, requestId, and production error redaction.
- win-x64 publish produced `artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe`.
- Published executable ran in console mode and returned healthy on `GET /api/v1/health`.
- Install/uninstall/certificate scripts were checked with `-WhatIf`; Windows Service was not installed by Codex.

## Tested With Hardware

Test date: 3 สิงหาคม 2569

- Reader: Identive SCR33xx v2.0 USB SC Reader 0
- Reader Detection: ผ่าน
- Card Presence: ผ่าน
- ATR: ผ่าน
- CardInserted: ผ่าน
- CardRemoved: ผ่าน
- Hardware Test: ผ่าน 1 Test
- ATR used for verification: `3B-79-96-00-00-54-48-20-4E-49-44-20-31-33`

Hardware API verified through the local API on `http://127.0.0.1:18442`:

- `GET /api/v1/readers`: returned `isConnected=true`, `isCardPresent=true`, and the ATR above.
- `GET /api/v1/card/status`: returned `CardPresent` and the ATR above.
- `POST /api/v1/card/atr`: returned the ATR above.

## Not Implemented Or Blocked

- อ่าน Citizen ID
- อ่านชื่อ
- อ่านวันเกิด
- อ่านที่อยู่
- อ่านรูปถ่าย
- Thai Card APDU Provider
- การเชื่อม Central Member API จริง

No Citizen ID, owner name, address, birth date, or photo has been read or documented.

## Security Limitations

- Production deployments must configure public JWT verification material or authority configuration; private signing keys must not be stored in the agent.
- Development key authentication is disabled outside Development environment.
- The local API is designed for loopback binding only.
