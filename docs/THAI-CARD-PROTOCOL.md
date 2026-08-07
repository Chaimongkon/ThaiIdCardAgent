# Thai Card Protocol

**This document has moved.** See [THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md) for the provider
architecture, the authorization rules that govern which implementations may exist, the read
contract, validation, and error codes.

## Status

`BLOCKED_OFFICIAL_PROTOCOL_REQUIRED`

No official Department of Provincial Administration technical material is present in this
repository. `POST /api/v1/card/read` returns HTTP 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED` through
`IThaiCardDataProvider` / `NotConfiguredThaiCardDataProvider`.

This repository intentionally contains **no guessed APDU commands**. A provider may be added only
after the command set, data-decoding rules, and usage rights are verified against official material.

## Phase 13A scope

Implemented (abstraction only — no card protocol):

- `IThaiCardDataProvider` seam, isolated from endpoints, PC/SC, JWT, SSE, and the web client
- Read orchestration: reader/card preconditions, timeout, cancellation, single in-flight read
- Citizen ID validation (13 digits + checksum), fail-closed, never repaired
- `card.read` permission requirement, audit records, sanitized errors
- Cooperative member verification flow, UI, and tests against a mock provider

Not implemented, and out of scope for Phase 13A:

- Thai name, English name, birth date, issue/expiry dates
- Address
- Photo
- Religion

Implemented and tested separately:

- Reader detection, card presence, ATR retrieval, reader/card monitor events

No document in this repository contains a real Citizen ID or cardholder personal data. Every citizen
ID used in tests is a synthetic, checksum-valid value.
