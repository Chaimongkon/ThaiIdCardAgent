# Development

## Baseline

```powershell
Get-Location
git status
dotnet build -c Release
dotnet test -c Release --filter "Category!=Hardware"
```

The repository path must be `D:\1.FrontEnd Framework\ThaiIdCardAgent`. Do not create a nested repository or overwrite the project.

On this machine, use `-m:1` when SDK 10.0.302 fails the solution parallel graph with no error text:

```powershell
dotnet build -c Release -m:1
dotnet test -c Release --filter "Category!=Hardware" -m:1
```

## Running The API

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Security__DevelopmentApiKey = "local-test-key"
dotnet run --project ".\src\ThaiIdCardAgent.Service"
```

Development HTTP listens on `http://127.0.0.1:18442` only. Production HTTP is disabled.

## Hardware Checks

```powershell
dotnet run --project ".\src\ThaiIdCardAgent.Console" -- readers
dotnet run --project ".\src\ThaiIdCardAgent.Console" -- status
dotnet run --project ".\src\ThaiIdCardAgent.Console" -- atr
dotnet run --project ".\src\ThaiIdCardAgent.Console" -- monitor
```

Hardware tests are separate and should be run only when a real reader/card is available and explicitly intended.

## Coding Rules

- Do not hard-code reader names or ATR values in implementation.
- Do not implement guessed Thai ID APDUs.
- Do not log or display Citizen ID, names, address, photo, or raw APDU responses.
- Keep PC/SC logic in `ThaiIdCardAgent.Pcsc`.
