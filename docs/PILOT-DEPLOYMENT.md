# Pilot Deployment

Pilot scope is limited to the tested workstation class and configuration until broader rollout evidence is collected.

## Confirmed On Test Machine

- Windows Service `ThaiIdCardAgent` installed and running.
- Service account: `NT AUTHORITY\LocalService`.
- PC/SC access under LocalService: passed.
- HTTPS health on `https://localhost:18443`: passed without certificate-validation bypass.
- JWT authentication and replay-safe fresh-token flow: passed.
- Readers API: passed.
- Card status API: passed.
- Card ATR API: passed.
- CardRemoved and CardInserted via status polling: passed.
- SSE CardRemoved and CardInserted via `/api/v1/events`: passed under Windows Service with real hardware.
- SSE disconnect and reconnect repeated rounds: passed.
- Windows reboot and Automatic Delayed Start: passed.
- Install, upgrade, uninstall preserving data, reinstall, and certificate retention: passed.

## Pilot Checklist

- Use a managed server-side JWT signing key; keep private key out of browser bundles and Git.
- Configure exact Agent CORS origins for the pilot web origin.
- Install certificate trust in the correct machine scope.
- Install the service as `NT AUTHORITY\LocalService` unless a formal security decision requires another account.
- Verify `GET /api/v1/health` after reboot.
- Verify readers/status/ATR through the service account on each hardware baseline.
- Verify SSE CardRemoved/CardInserted separately from status polling.
- Capture only non-PII diagnostics: service status, endpoint status codes, reader names, event types, timestamps, and ATR hex.

## Still Incomplete

- Executable/installer code signing is not implemented; published binaries are unsigned.
- Thai card APDU/data reading is not implemented and `/api/v1/card/read` returns `THAI_CARD_PROTOCOL_NOT_CONFIGURED`.
- Wider enterprise rollout has not been validated on every target workstation image, driver version, endpoint security policy, or reader/card combination.

## Web Example Manual Acceptance

Before committing the Phase 10 web integration as pilot-ready, run the Next.js example against the installed service and real hardware:

1. Start `examples/nextjs-client` with `.env.local` pointing to a server-side test private key.
2. Open the browser app.
3. Check Agent.
4. Refresh Readers and verify reader name/count.
5. Remove card and verify SSE `CardRemoved`; verify status shows `NoCard`.
6. Insert card and verify SSE `CardInserted`; verify status shows `CardPresent`.
7. Read ATR and verify uppercase hex only.
8. Connect/disconnect SSE at least 3 rounds.
9. Confirm JWT is not in URL, `localStorage`, `sessionStorage`, console output, or logs.
10. Confirm private key text is not present in the production browser bundle.
