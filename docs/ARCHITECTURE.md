# Architecture

ThaiIdCardAgent is split into small projects:

- `ThaiIdCardAgent.Core`: contracts, result/error models, privacy helpers, and exceptions.
- `ThaiIdCardAgent.Pcsc`: Windows PC/SC integration, reader state mapping, ATR formatting, and polling monitor.
- `ThaiIdCardAgent.ThaiCard`: Thai ID card reader abstraction. The current provider is intentionally not configured.
- `ThaiIdCardAgent.Service`: ASP.NET Core Minimal API and Windows Service host.
- `ThaiIdCardAgent.Console`: diagnostics and hardware verification commands.

```mermaid
flowchart TD
    Client[Authorized local client] --> Api[Service /api/v1]
    Api --> Auth[Development key or JWT auth]
    Api --> ReaderService[ISmartCardReaderService]
    Api --> Monitor[ISmartCardMonitor]
    Api --> ThaiReader[IThaiIdCardReader]
    ReaderService --> Pcsc[IPcscPlatform]
    Monitor --> ReaderService
    Pcsc --> WinSCard[winscard.dll / PCSC]
    ThaiReader --> NotConfigured[THAI_CARD_PROTOCOL_NOT_CONFIGURED]
```

The Service project uses dependency injection for `ISmartCardReaderService`, `ISmartCardMonitor`, and `IThaiIdCardReader`. Endpoints must not call PC/SC APIs directly.

Reader availability is independent from card connection. PC/SC states are flags and are mapped with bitwise checks in `PcscStateMapper`.
