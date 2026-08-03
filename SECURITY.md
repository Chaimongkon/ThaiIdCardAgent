# Security

ThaiIdCardAgent binds only to loopback addresses and must not be exposed on `0.0.0.0`.

Do not commit development keys, JWT validation keys, certificates, card data, APDU traces, JWT values, or Authorization headers. Production signing keys must stay outside the local agent.

Logs must not contain full citizen IDs, names, birth dates, addresses, photos, raw APDU responses, JWTs, API keys, or Authorization headers.