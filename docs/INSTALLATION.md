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

Uninstall as Administrator:

```powershell
.\scripts\Uninstall-Service.ps1 -WhatIf
.\scripts\Uninstall-Service.ps1
```

Uninstall removes only the agent program folder by default and keeps config/logs. Use `-RemoveData` only when config/log deletion is intended. Certificates are not deleted automatically.

Do not report Windows Service installation as successful unless the install command was actually run and health check passed on the target machine.

For a controlled upgrade where another step will start the service, use `-SkipStart`. Do not report a service-account hardware test as passed until the installed Windows Service answers the authenticated API calls with the real reader/card.
