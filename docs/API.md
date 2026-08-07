# API

Base URL:

- Development: `http://127.0.0.1:18442`
- Production: `https://localhost:18443`

The service binds loopback only. Do not bind to `0.0.0.0`, `*`, `+`, or LAN addresses.

## Response Shape

Success responses except health use:

```json
{
  "success": true,
  "data": {},
  "error": null,
  "requestId": "..."
}
```

Error responses use:

```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "CARD_NOT_PRESENT",
    "message": "..."
  },
  "requestId": "..."
}
```

Production error responses do not include stack traces, inner exception details, local file paths, secrets, or PII.

## Endpoints

### GET `/api/v1/health`

Anonymous. Returns service health only:

```json
{
  "status": "healthy",
  "service": "ThaiIdCardAgent",
  "version": "1.0.0.0",
  "utcTime": "..."
}
```

Health never returns reader names, card status, ATR, card data, or secret configuration.

### GET `/api/v1/info`

Authenticated. Returns agent metadata, `thaiCardProtocol` (`configured` / `not_configured`),
`thaiCardProvider` (the registered provider name), and `cardReadPermissionGranted` for the calling
token.

### GET `/api/v1/readers`

Authenticated. Returns real `ISmartCardReaderService` data. Reader names and ATR values are never hard-coded.

### GET `/api/v1/card/status?readerName=...`

Authenticated. If `readerName` is omitted and one reader exists, the API auto-selects it. If multiple readers exist, it returns `READER_SELECTION_REQUIRED`. Missing readers return `READER_NOT_FOUND`.

### POST `/api/v1/card/atr`

Authenticated body:

```json
{ "readerName": null, "requestId": "optional-client-id" }
```

No card returns HTTP 422 `CARD_NOT_PRESENT`. Concurrent reads for the same reader return HTTP 409 `AGENT_BUSY`.

### POST `/api/v1/card/read`

Phase 13A identity read. Requires authentication **and** the `card.read` permission (a `scope` or
`permissions` claim containing `card.read`). A token without it returns HTTP 403 `FORBIDDEN`.

Request body:

```json
{ "readerName": null, "requestId": "optional-client-id" }
```

Success response data — the citizen ID and nothing else about the cardholder:

```json
{
  "verificationId": "…",
  "readerName": "Reader A",
  "citizenId": "<13 digits>",
  "readAtUtc": "…",
  "providerName": "…",
  "cardAtr": null
}
```

There is no field for photo, address, name, birth date, or religion — Phase 13A reads the citizen ID
only.

Behaviour:

- Explicit user action only; there is no polling or background variant.
- One read at a time agent-wide: a concurrent read returns HTTP 409 `AGENT_BUSY` rather than
  queueing, so a double submission cannot become two reads of the same card.
- The reader must exist and a card must be present before any card command is issued.
- Configurable timeout (`Agent:CardRead:TimeoutSeconds`, default 15) and caller cancellation.
- `Cache-Control: no-store` is set on the response.
- The citizen ID is validated (13 digits + checksum) and **never repaired**; failure returns HTTP 422
  `CARD_DATA_INVALID` without echoing the value.
- The citizen ID is returned in the response body and is **never** written to logs. Audit records
  carry only the masked form and an optional keyed correlation hash.

**Currently returns HTTP 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED`**, because no authorized provider
is configured. See [THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md).

### GET `/api/v1/events`

Authenticated Server-Sent Events. Events include `eventType`, `readerName`, `cardPresent`, `atr` when appropriate, and `occurredAtUtc`. The stream must not contain Citizen ID, names, address, photo, or raw APDU responses.

## Error Mapping

- 400 `INVALID_REQUEST`
- 401 `UNAUTHORIZED`
- 403 `FORBIDDEN`
- 404 `READER_NOT_FOUND`
- 409 `AGENT_BUSY`
- 409 `CARD_REMOVED`
- 409 `CARD_REMOVED_DURING_READ`
- 422 `CARD_NOT_PRESENT`
- 422 `READER_SELECTION_REQUIRED`
- 422 `CARD_DATA_INVALID`
- 500 `INTERNAL_ERROR`
- 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED`
- 502 `CARD_COMMUNICATION_ERROR`
- 503 `SMART_CARD_SERVICE_UNAVAILABLE`
- 503 `READER_UNAVAILABLE`
- 503 `PROVIDER_UNAVAILABLE`
- 504 `TIMEOUT`
- 504 `CARD_READ_TIMEOUT`
