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

Authenticated. Returns agent metadata and `thaiCardProtocol: "not_configured"`.

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

Authenticated. Current implementation uses `IThaiIdCardReader`, which is intentionally `NotConfiguredThaiIdCardReader`, and returns HTTP 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED`.

### GET `/api/v1/events`

Authenticated Server-Sent Events. Events include `eventType`, `readerName`, `cardPresent`, `atr` when appropriate, and `occurredAtUtc`. The stream must not contain Citizen ID, names, address, photo, or raw APDU responses.

## Error Mapping

- 400 `INVALID_REQUEST`
- 401 `UNAUTHORIZED`
- 403 `FORBIDDEN`
- 404 `READER_NOT_FOUND`
- 409 `AGENT_BUSY`
- 409 `CARD_REMOVED`
- 422 `CARD_NOT_PRESENT`
- 422 `READER_SELECTION_REQUIRED`
- 500 `INTERNAL_ERROR`
- 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED`
- 503 `SMART_CARD_SERVICE_UNAVAILABLE`
- 503 `READER_UNAVAILABLE`
- 504 `TIMEOUT`
