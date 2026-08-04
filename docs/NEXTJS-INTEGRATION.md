# Next.js Integration

The runnable example is in `examples/nextjs-client`.

```powershell
cd ".\examples\nextjs-client"
npm ci
copy .env.example .env.local
npm run dev
```

The example includes:

- `lib/thai-id-agent-client.ts`: typed browser client for Agent JSON APIs.
- `lib/sse.ts`: fetch-streaming SSE parser/client with Authorization header support.
- `lib/local-agent-jwt.ts`: server-side RS256 JWT issuing helper.
- `app/api/local-agent/token/route.ts`: token broker endpoint with `Cache-Control: no-store`.
- `components/ThaiIdAgentPanel.tsx`: operational UI for health, readers, status, ATR, and latest SSE event.

## Security Rules

- The private signing key stays server-side and must not use a `NEXT_PUBLIC_` variable.
- Browser code requests a fresh JWT from `POST /api/local-agent/token` for every protected Agent API request.
- SSE uses `fetch` streaming because `EventSource` cannot send an `Authorization` header.
- Every SSE reconnect gets a fresh JWT due Agent replay protection.
- JWTs are not stored in `localStorage`, `sessionStorage`, URLs, or logs.
- The UI does not call `/api/v1/card/read`; the Card Read button is disabled because Thai card protocol is not configured.

## Supported Agent Calls

- `GET /api/v1/health`
- `GET /api/v1/readers`
- `GET /api/v1/card/status?readerName=...`
- `POST /api/v1/card/atr`
- `GET /api/v1/events`

The current `/api/v1/card/read` behavior is HTTP 501 `THAI_CARD_PROTOCOL_NOT_CONFIGURED`.

## Validation

Automated validation for the example:

```powershell
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

Manual validation must use the installed Windows Service and real hardware. Do not count mocked tests or status polling as SSE acceptance.
