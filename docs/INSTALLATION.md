# Installation

Publish first:

```powershell
.\scripts\Publish-WinX64.ps1
```

Install as Administrator:

```powershell
.\scripts\Install-Service.ps1 -WhatIf
.\scripts\Install-Service.ps1
```

Uninstall as Administrator:

```powershell
.\scripts\Uninstall-Service.ps1 -WhatIf
.\scripts\Uninstall-Service.ps1
```

The uninstall script keeps config and logs unless `-RemoveData` is specified.
