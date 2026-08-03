# Architecture

The solution is split into Core, PCSC, ThaiCard, Console, and Service projects.

- Core owns domain models, interfaces, errors, and PII redaction.
- PCSC implements reader detection, card presence, ATR, and monitoring through Windows PC/SC.
- ThaiCard contains the public Thai ID card reader interface and a not-configured implementation.
- Console provides diagnostics for operators and developers.
- Service exposes a loopback ASP.NET Core Minimal API and can run as a Windows Service.

Core does not reference PCSC, Console, or Service.
