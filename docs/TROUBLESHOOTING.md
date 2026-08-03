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

SDK 10.0.302 on this machine can fail the solution parallel graph without an error message. Rerun with `-m:1`.

## HTTPS Certificate

Use `scripts\New-AgentCertificate.ps1 -WhatIf` first. Do not disable certificate validation in clients.
