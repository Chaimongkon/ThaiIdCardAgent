# Configuration

## Development

Development authentication uses header `X-Agent-Development-Key`. Configure the key outside Git:

```powershell
dotnet user-secrets set "Security:DevelopmentApiKey" "local-test-key" --project ".\src\ThaiIdCardAgent.Service"
$env:Security__DevelopmentApiKey = "local-test-key"
```

Development HTTP is loopback-only on `http://127.0.0.1:18442`. Development CORS allows only exact configured origins, with `http://localhost:3000` in `appsettings.Development.json`.

## Production

Production uses HTTPS loopback `https://localhost:18443`; HTTP is disabled. Use `127.0.0.1` only when the certificate contains IP SAN `127.0.0.1`. JWT validation requires:

- issuer
- audience `thai-id-card-agent`
- signature
- expiration
- not-before
- `jti`
- `sub`
- `workstation_id`
- maximum lifetime 60 seconds

Configure only public verification material or authority configuration in the agent. Signing private keys must stay outside the agent.

Supported public key configuration examples:

```powershell
$env:Agent__Jwt__PublicKeyPath = 'C:\ProgramData\ThaiIdCardAgent\Config\jwt-public.pem'
$env:Security__Jwt__PublicKeyPem = '<public-pem-only>'
```

## CORS

Allowed origins must be exact strings. Wildcards, `AllowAnyOrigin`, and origin reflection are not allowed.

## PC/SC

`Pcsc:TimeoutSeconds` defaults to 10 seconds and must stay between 1 and 120 seconds. Reader-level operations use per-reader locking in the PC/SC service, not a global lock.

## Paths

- Program: `C:\Program Files\ThaiIdCardAgent`
- Config: `C:\ProgramData\ThaiIdCardAgent\Config`
- Logs: `C:\ProgramData\ThaiIdCardAgent\Logs`
