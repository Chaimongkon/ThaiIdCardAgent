# Troubleshooting

## `ERR_CONNECTION_REFUSED` on `/api/v1/health`

The service is not running or is not listening on the expected loopback port. Start Development mode:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Security__DevelopmentApiKey = "local-test-key"
dotnet run --project ".\src\ThaiIdCardAgent.Service"
```

Then call `http://127.0.0.1:18442/api/v1/health`.

## `401 UNAUTHORIZED`

For Development endpoints other than health, include:

```powershell
-Headers @{ "X-Agent-Development-Key" = "local-test-key" }
```

For Production, use a valid short-lived JWT with `jti`, `sub`, and `workstation_id`.

## `CARD_NOT_PRESENT`

The reader is available but no card is detected. Confirm with:

```powershell
dotnet run --project ".\src\ThaiIdCardAgent.Console" -- status
```

## `READER_SELECTION_REQUIRED`

Multiple readers are connected. Provide `readerName` explicitly.

## `SMART_CARD_SERVICE_UNAVAILABLE`

Start or repair the Windows Smart Card Service. Verify Windows can see the card with `certutil -scinfo` when needed.

## Build Fails With 0 Errors

Inside the Codex managed sandbox, SDK 10.0.302 can fail the solution parallel graph without an error message. The same standard commands passed outside the sandbox. See `docs/BUILD-TROUBLESHOOTING.md` before changing build defaults or using `-m:1` as a workaround.

## HTTPS Certificate

Use `scripts\New-AgentCertificate.ps1 -WhatIf` first. Do not disable certificate validation in clients.

## Production diagnostics

Run diagnostics without opening an HTTP/HTTPS listener:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
.\ThaiIdCardAgent.Service.exe --diagnostics
```

The command reports configuration status only. It must not print development keys, JWTs, certificate passwords, private keys, Authorization headers, or cardholder PII.

## LocalService cannot access PC/SC

Do not switch automatically to LocalSystem. Capture the real API error code, confirm `SCardSvr` is running, verify the service account required by the organization, and check whether the installed service account can access the reader through `/api/v1/readers`, `/api/v1/card/status`, and `/api/v1/card/atr`.

## HTTPS TLS handshake fails with `SEC_E_NO_CREDENTIALS`

A Production console-mode test can listen on `https://localhost:18443` but still fail TLS handshake from PowerShell or `curl.exe` with `SEC_E_NO_CREDENTIALS (0x8009030e)`. Do not use `-SkipCertificateCheck` to hide this. Verify the certificate has Server Authentication EKU, a SAN matching the host being called, and a private key usable by the service account. Re-run `ThaiIdCardAgent.Service.exe --diagnostics` and test again with a trusted certificate.
