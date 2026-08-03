# Security

## Local Binding

The API is designed for loopback only:

- Development HTTP: `http://127.0.0.1:18442`
- Production HTTPS: `https://127.0.0.1:18443`

Do not expose the service on LAN interfaces.

## Authentication

Development uses `X-Agent-Development-Key` from `Security:DevelopmentApiKey`, user secrets, or `Security__DevelopmentApiKey`. It is valid only in Development.

Production uses JWT validation. Tokens must be signed, have issuer/audience validation, include `jti`, `sub`, and `workstation_id`, and expire within 60 seconds. Replay detection stores `jti` in memory until expiration.

The agent must only hold public verification material or authority metadata. Private signing keys must not be stored in the agent.

## CORS

CORS allows exact configured origins only. Wildcards and origin reflection are not allowed.

## Data Handling

The current implementation reads reader state and ATR only. It does not read Citizen ID, names, birth date, address, or photo. The API and event stream must not expose PII or raw APDU responses.

## Error Handling

Production error responses must not include stack traces, inner exceptions, local paths, secrets, or PII. Development may include technical details for diagnostics.
