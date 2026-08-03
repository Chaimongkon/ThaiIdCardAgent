# Implementation Status

## Implemented

- .NET 10 solution and project structure.
- Core models, interfaces, result model, exceptions, error mapping, and PII redaction.
- Windows PC/SC reader listing, card presence, ATR, and polling monitor.
- In-memory PC/SC adapter for tests.
- Console commands: `readers`, `status`, `atr`, `monitor`, `diagnostics`, `read`.
- Minimal API and Windows Service hosting configuration.
- Development key auth, production JWT validation, replay detection, and exact-origin CORS.
- Publish/install/start/stop/uninstall/certificate/test PowerShell scripts.
- Next.js client example.

## Tested without hardware

- Restore and Release build.
- Non-hardware tests: Core 3, PCSC 8, Service 11.
- Development API health endpoint over HTTP loopback `18442`.
- win-x64 publish to `artifacts/publish/win-x64`.
- Unit tests for redaction, result/error mapping, fake reader scenarios, ATR, no-card, missing-reader, service failure, and busy reader.
- Integration tests for health, auth, JWT failures, replay, CORS, and protocol-not-configured response.

## Tested with hardware

- Not tested in this workspace.

## Not implemented

- Thai ID card data APDU protocol and real card data parsing.
- Production public-key certificate validation workflow beyond configuration boundary.

## Blocked by external dependency

- Verified Thai ID card protocol provider and real smart card hardware validation.

## Security limitations

- Integration tests use a configured symmetric JWT validation key. Production should validate with a public key while signing remains outside the agent.
- Hardware tests return early unless `THAI_ID_AGENT_HARDWARE_TESTS=1` is set.
