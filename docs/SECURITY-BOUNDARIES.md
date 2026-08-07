# Security Boundaries

ThaiIdCardAgent separates browser UI, token issuance, local Agent APIs, and PC/SC hardware access.

## Browser Boundary

Allowed in browser memory:

- Short-lived JWT returned by the backend token broker for the current request only.
- Reader names.
- Card status.
- ATR in uppercase hex form.
- SSE event type and timestamp.
- The citizen ID **transiently**, as a local variable, for the duration of one verification request
  (Phase 13A). It must never reach React state, a ref, storage, a URL, or the console.
- Member data returned by the cooperative database (member id, member no, full name) and the
  **masked** citizen ID.

Not allowed in browser storage, URLs, logs, screenshots, or committed files:

- JWTs.
- Private signing keys.
- PFX/P12 files or certificate passwords.
- Development keys.
- The raw citizen ID.
- Names from the card, address, birth date, photo, religion, or APDU responses.

Sensitive UI state is cleared when a `CardRemoved` event arrives, so the next person at the counter
cannot see the previous holder's record.

## Token Broker Boundary

The token broker runs server-side only. It signs RS256 JWTs with issuer `thai-id-card-agent-client`, audience `thai-id-card-agent`, required `jti`, `sub`, and `workstation_id` claims, and lifetime no longer than 60 seconds.

The private signing key must come from a server-side environment variable or server-side file path. It must never use a `NEXT_PUBLIC_` variable and must never be sent to the browser.

## Local Agent Boundary

ThaiIdCardAgent binds loopback only. Production uses HTTPS on `https://localhost:18443`, exact-origin CORS, JWT authentication, and replay detection. The service does not require mTLS: `ClientCertificateMode.NoCertificate`.

The Agent exposes ATR and reader/card state. Thai card identity reading (`POST /api/v1/card/read`)
is implemented as a provider abstraction but has **no authorized provider**, so it fails closed with
`THAI_CARD_PROTOCOL_NOT_CONFIGURED`. It must remain disabled until a provider built from official
Department of Provincial Administration material is added and validated against real hardware. See
[THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md).

Card reading additionally requires the `card.read` permission in the JWT (`scope` or `permissions`
claim). Authentication alone is not sufficient: a token authorized only for reader status cannot
read identity data off a card.

## Card Data Boundary

Phase 13A reads the **13-digit citizen ID only**. Photo, address, name, birth date, and religion are
not read and have no field in any contract to land in.

| Location | Raw citizen ID | Masked citizen ID | Keyed correlation hash |
| --- | --- | --- | --- |
| Card read API response | Yes (in the body only) | — | — |
| Agent logs / audit records | **Never** | Yes | When a key is configured |
| Browser state or storage | **Never** | Yes | — |
| Verification API response | **Never** (no such field) | Yes | — |
| Server-side audit | **Never** | Yes | When a key is configured |

Correlation across audit records uses a keyed HMAC, never a plain hash: a citizen ID has only about
10¹² valid values, so an unkeyed digest is reversible by exhaustive search and would be a reversible
encoding rather than a protection. With no key configured, no hash is produced at all.

Malformed card data fails the read. A citizen ID is never repaired, and no part of a rejected value
appears in any error message.

## Certificate Boundary

The server certificate is in `Cert:\LocalMachine\My` and the public certificate must be trusted in the correct machine trust scope for machine-context clients. Certificate validation bypass is not acceptable for production or pilot acceptance.

## Repository Boundary

Do not commit real `.env` files, JWTs, private keys, PFX/P12 files, certificate passwords, machine-specific secret config, or cardholder PII. Public local test certificates are machine-specific artifacts and should normally remain ignored.

Specifically never commit a **real citizen ID**, real card data, a raw card dump, an APDU trace
containing personal data, or a screenshot showing personal data — including in tests, fixtures, and
documentation. Every citizen ID in this repository is a synthetic, checksum-valid value used for
testing.
