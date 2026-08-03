# Security Details

- API binding is loopback-only.
- CORS uses exact configured origins; wildcard origins are rejected by options validation.
- Development key auth is available only in Development.
- Production JWT validation checks issuer, audience, signature, expiration, not-before, `jti`, `sub`, and `workstation_id`.
- `jti` values are cached until token expiration to reject replay.
- The agent must not hold a private signing key.
- PII redaction masks 13-digit citizen IDs as `1-2345-xxxxx-12-3`.
