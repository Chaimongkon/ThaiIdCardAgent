# Installation

Publish first:

```powershell
.\scripts\Publish-WinX64.ps1
```

Output is written to `artifacts\publish\win-x64` and must contain `ThaiIdCardAgent.Service.exe`.

Run pre-install diagnostics before installing:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
.\artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe --diagnostics
```

Install as Administrator:

```powershell
.\scripts\Install-Service.ps1 -WhatIf
.\scripts\Install-Service.ps1
```

The install script:

- installs to `C:\Program Files\ThaiIdCardAgent`
- keeps config/logs under `C:\ProgramData\ThaiIdCardAgent`
- backs up existing config
- grants LocalService access to ProgramData
- creates service `ThaiIdCardAgent`
- uses display name `Thai ID Card Local Agent`
- runs as `NT AUTHORITY\LocalService`
- sets Automatic Delayed Start
- sets restart recovery actions after 60 seconds
- starts the service and checks health
- supports upgrade/reinstall by stopping an existing service before copying files

## Production Acceptance

Production Acceptance has passed on the test machine with the service running as `NT AUTHORITY\LocalService`.

Validated through the installed Windows Service:

- HTTPS health without certificate-validation bypass.
- JWT runtime issue.
- Readers API.
- Card status API.
- Card ATR API.
- PC/SC reader access under `NT AUTHORITY\LocalService`.
- CardRemoved via `/api/v1/card/status` polling until `NoCard` was observed 2 consecutive times.
- CardInserted via `/api/v1/card/status` polling until `CardPresent` was observed 2 consecutive times.
- Restart service health/readers.
- Upgrade.
- Uninstall while keeping config/logs.
- Reinstall.
- Certificate retention.

Still not tested:

- SSE `CardRemoved` through `/api/v1/events`.
- SSE `CardInserted` through `/api/v1/events`.
- Windows restart and Automatic Delayed Start after reboot.

Executable/installer code signing is not implemented yet; published binaries are unsigned.

## Acceptance Command

Run from an elevated PowerShell session on the target workstation:

```powershell
.\scripts\Test-ProductionAcceptance.ps1 `
    -CertificateThumbprint "<server-certificate-thumbprint>" `
    -CertificateHostName "localhost" `
    -BaseUrl "https://localhost:18443" `
    -JwtPublicKeyPath "<public-verification-key-path>" `
    -JwtPrivateKeyPath "<test-private-signing-key-path>"
```

Use test signing material only for acceptance. Do not store JWTs, private keys, PFX/P12 files, passwords, or cardholder data in Git, docs, screenshots, logs, or tickets.

Uninstall as Administrator:

```powershell
.\scripts\Uninstall-Service.ps1 -WhatIf
.\scripts\Uninstall-Service.ps1
```

Uninstall removes only the agent program folder by default and keeps config/logs. Use `-RemoveData` only when config/log deletion is intended. Certificates are not deleted automatically.

For a controlled upgrade where another step will start the service, use `-SkipStart`.