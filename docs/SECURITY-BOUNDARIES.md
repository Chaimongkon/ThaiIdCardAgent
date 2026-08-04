# Security Boundaries

ThaiIdCardAgent separates browser UI, token issuance, local Agent APIs, and PC/SC hardware access.

## Browser Boundary

Allowed in browser memory:

- Short-lived JWT returned by the backend token broker for the current request only.
- Reader names.
- Card status.
- ATR in uppercase hex form.
- SSE event type and timestamp.

Not allowed in browser storage, URLs, logs, screenshots, or committed files:

- JWTs.
- Private signing keys.
- PFX/P12 files or certificate passwords.
- Development keys.
- Citizen ID, names, address, birth date, photo, or APDU responses.

## Token Broker Boundary

The token broker runs server-side only. It signs RS256 JWTs with issuer `thai-id-card-agent-client`, audience `thai-id-card-agent`, required `jti`, `sub`, and `workstation_id` claims, and lifetime no longer than 60 seconds.

The private signing key must come from a server-side environment variable or server-side file path. It must never use a `NEXT_PUBLIC_` variable and must never be sent to the browser.

## Local Agent Boundary

ThaiIdCardAgent binds loopback only. Production uses HTTPS on `https://localhost:18443`, exact-origin CORS, JWT authentication, and replay detection. The service does not require mTLS: `ClientCertificateMode.NoCertificate`.

The Agent exposes ATR and reader/card state only. Thai card personal-data reading is not implemented and must remain disabled until a verified protocol provider is added.

## Certificate Boundary

The server certificate is in `Cert:\LocalMachine\My` and the public certificate must be trusted in the correct machine trust scope for machine-context clients. Certificate validation bypass is not acceptable for production or pilot acceptance.

## Repository Boundary

Do not commit real `.env` files, JWTs, private keys, PFX/P12 files, certificate passwords, machine-specific secret config, or cardholder PII. Public local test certificates are machine-specific artifacts and should normally remain ignored.
