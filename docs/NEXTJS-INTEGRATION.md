# Next.js Integration

Use `examples/nextjs-client/thai-id-agent-client.ts` from a client component or a browser-side helper that can obtain a short-lived credential.

The example supports:

- `getAgentHealth()`
- `getReaders(tokenOrDevelopmentKey)`
- `getCardStatus(tokenOrDevelopmentKey, readerName?)`
- `readCardAtr(tokenOrDevelopmentKey, readerName?)`
- `readThaiIdCard(tokenOrDevelopmentKey, options, readerName?)`
- `subscribeReaderEvents(tokenOrDevelopmentKey, handlers)`

`tokenOrDevelopmentKey` can be a Development key or a JWT. A JWT-like string is sent as `Authorization: Bearer ...`; other strings are sent as `X-Agent-Development-Key`. For stricter code, pass `{ type: "bearerToken", value }` or `{ type: "developmentKey", value }`.

Security rules for clients:

- use `fetch` with `AbortController`
- set request timeouts
- do not use `localStorage` or `sessionStorage` for tokens or card data
- do not place card data in URLs
- do not log card data
- clear React state on unmount, logout, new read, and modal close

Current card-data reading is not supported. UI must not claim Citizen ID, names, address, birth date, or photo can be read until a verified provider is implemented.
