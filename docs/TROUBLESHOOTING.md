# Troubleshooting

Use the console diagnostics command first:

```powershell
dotnet run --project "src\ThaiIdCardAgent.Console\ThaiIdCardAgent.Console.csproj" -- diagnostics
```

Common issues:

- No readers: verify USB reader driver and Windows Smart Card Service.
- No card: insert a card and rerun `status` or `atr`.
- Service unavailable: start the Windows Smart Card Service.
- API 401: verify development key or production JWT.
- API 501 on card read: Thai ID card protocol provider is not configured.
