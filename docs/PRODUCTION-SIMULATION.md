# Phase 8 Controlled Production Simulation

Date: 2026-08-03
Repository: `D:\1.FrontEnd Framework\ThaiIdCardAgent`
Branch: `main`
Scope: Controlled Production Simulation for HTTPS, JWT, CORS, Windows Service readiness, and hardware/API behavior.

## Result

Recommendation: **No-Go for unattended Production rollout**.

Production diagnostics passed for certificate, JWT public key path, CORS configuration, SCardSvr, and PC/SC reader detection. The final published service started and listened on `https://localhost:18443`, but HTTPS client handshakes from .NET `HttpClient`, Windows PowerShell, and `curl.exe` failed with Schannel `SEC_E_NO_CREDENTIALS`. Therefore runtime HTTPS/JWT/CORS/API verification through the final published service is not marked as passed.

Real Windows Service installation and LocalService hardware access were not run because the current session is not elevated Administrator and the service is not installed.

## HTTPS/TLS

Configured certificate:

- Store: `LocalMachine\My`
- Thumbprint: `79A7A07FDF6BC7EEBD3BC6F113659B79537A7101`
- Subject/issuer: `CN=localhost`
- SAN: `localhost`
- EKU: Server Authentication
- Private key: present and usable by the current console process
- Chain: trusted by the current machine

Observed behavior:

- Published service listened on `https://localhost:18443`.
- Kestrel bound loopback `127.0.0.1:18443` and `[::1]:18443`.
- Production diagnostics returned pass for SAN `localhost`, private key usability, and chain trust.
- .NET `HttpClient` probe failed before HTTP response with `SEC_E_NO_CREDENTIALS`.
- Windows PowerShell and `curl.exe` show the same Schannel failure on this host.
- No `-SkipCertificateCheck` and no custom certificate callback were used.
- `https://127.0.0.1:18443` is not a valid production target for the current certificate because the certificate does not contain IP SAN `127.0.0.1`.

Implementation change: Production startup now fails fast if the configured certificate is missing, expired/not-yet-valid, missing a usable private key, missing Server Authentication EKU, or missing SAN/host match for `localhost`.

## JWT

Code and tests now support public verification key material from a PEM path via `Agent:Jwt:PublicKeyPath`, `Security:Jwt:PublicKeyPath`, `Agent__Jwt__PublicKeyPath`, or `Security__Jwt__PublicKeyPath`.

Validated by integration tests and the test-token generator:

- Valid short-lived JWT from a public key path is accepted in service integration tests.
- Expired JWT is rejected.
- Wrong audience is rejected.
- Missing `jti`, `sub`, or `workstation_id` is rejected.
- Replay of the same `jti` is rejected.
- Lifetime greater than 60 seconds is rejected by the runtime handler and test generator requires `-AllowInvalidLifetime` to create such a negative-test token.

Runtime JWT over HTTPS was not re-verified in the final published build because the TLS client handshake failed first.

No token or private key is stored in Git or printed by `scripts\New-TestJwt.ps1`.

## CORS

Configured allowed origin for simulation: `https://localhost:3000`.

Validated by tests:

- Allowed origin returns `Access-Control-Allow-Origin`.
- Unknown origin does not return the allow-origin header.
- Wildcards remain rejected by options validation.

Runtime CORS over HTTPS was not re-verified in the final published build because the TLS client handshake failed first.

## API And Hardware

Console CLI hardware checks with a real reader/card returned:

- `readers`: reader count `1`, `Connected: True`, `Card present: True`, ATR `3B-79-96-00-00-54-48-20-4E-49-44-20-31-33`.
- `status`: `connected=True`, `cardPresent=True`, same ATR.
- `atr`: same ATR.

Runtime API calls through HTTPS were blocked by the TLS client handshake failure in the final published build.

No Thai ID APDU implementation was added. No Citizen ID, name, address, birth date, or photo was read.

## Windows Service

Status in this session:

- Current process is not Administrator.
- `ThaiIdCardAgent` Windows Service is not installed.
- Real install: **blocked**.
- Real upgrade: **not tested**.
- Real uninstall: **not tested**.
- Service recovery action verification: **not tested**.
- Restart Windows auto-start verification: **not tested**.
- LocalService PC/SC access: **not tested**.
- Monitor `CardInserted`/`CardRemoved` through the installed service: **not tested**.

Administrator commands for the target machine:

```powershell
.\scripts\Publish-WinX64.ps1
.\scripts\Set-CertificatePrivateKeyAcl.ps1 -Thumbprint "79A7A07FDF6BC7EEBD3BC6F113659B79537A7101" -Account "NT AUTHORITY\LOCAL SERVICE" -RemoveBroadReadGroups
.\scripts\Install-Service.ps1 -HealthUri "https://localhost:18443/api/v1/health"
Get-Service ThaiIdCardAgent
sc.exe qc ThaiIdCardAgent
sc.exe qfailure ThaiIdCardAgent
```

## Private Key ACL

The current certificate private key file exists and is usable by the current console process, but its ACL includes broad read principals (`Everyone` / `BUILTIN\Users`) and LocalService-specific access was not proven in this non-admin session.

Required before Production rollout:

- Grant private key read access to `NT AUTHORITY\LOCAL SERVICE` or the configured service account.
- Remove broad read grants that are not operationally required.
- Re-run service diagnostics and HTTPS health from the installed service.

## Build And Tests

The exact `dotnet clean` solution-level command failed in this Codex-managed environment with `0 Warning(s), 0 Error(s)` after cleaning the tool project. The same pipeline passed with single-node MSBuild and node reuse disabled:

```powershell
dotnet clean -m:1 /nr:false
dotnet restore -m:1 /nr:false
dotnet build -c Release -m:1 /nr:false --no-restore
dotnet test -c Release -m:1 /nr:false --no-build --filter "Category!=Hardware"
```

Final non-hardware test result: 52 passed, 0 failed.

## Code Signing

The published executable is currently unsigned. Production distribution should add Authenticode signing before rollout.

## Open Items

- Resolve Schannel `SEC_E_NO_CREDENTIALS` for local HTTPS clients without bypassing certificate validation.
- Run real Windows Service install as Administrator.
- Verify HTTPS/JWT/CORS through the installed Windows Service.
- Verify PC/SC reader/card/ATR under `NT AUTHORITY\LOCAL SERVICE`.
- Verify monitor `CardInserted` and `CardRemoved` by physically removing/inserting the card while the monitor is running.
- Verify service restart, Windows restart, upgrade, and uninstall.
- Replace test JWT issuer with the real production issuer/key management process.
- Add code signing.