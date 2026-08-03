# API

Base path: `/api/v1`.

- `GET /health`: anonymous health status, no reader or card data.
- `GET /info`: authenticated agent metadata.
- `GET /readers`: authenticated reader list.
- `GET /card/status?readerName=`: authenticated card presence and ATR status.
- `POST /card/atr`: authenticated ATR read, body `{ "readerName": "optional" }`.
- `POST /card/read`: authenticated, returns HTTP 501 and `THAI_CARD_PROTOCOL_NOT_CONFIGURED`.
- `GET /events`: authenticated server-sent reader/card events.

Error responses include `requestId`, `code`, and `message`. Production responses do not include stack traces.
