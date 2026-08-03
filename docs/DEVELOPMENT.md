# Development

Use PowerShell from the repository root.

```powershell
dotnet restore "ThaiIdCardAgent.sln"
dotnet build "ThaiIdCardAgent.sln" -c Release
dotnet test "ThaiIdCardAgent.sln" -c Release --filter "Category!=Hardware"
```

Do not disable `TreatWarningsAsErrors`. Do not add APDU commands unless they come from a verified provider and have been tested with real Thai ID card hardware.
