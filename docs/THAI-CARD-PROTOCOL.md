# Thai Card Protocol

Thai card personal-data reading is not configured.

`POST /api/v1/card/read` currently returns HTTP 501 with `THAI_CARD_PROTOCOL_NOT_CONFIGURED` through `IThaiIdCardReader` and `NotConfiguredThaiIdCardReader`.

This repository intentionally does not include guessed APDU commands. A provider must be added only after the command set, data decoding rules, and usage rights are verified.

Not implemented:

- Citizen ID
- Thai name
- English name
- birth date
- issue/expiry dates
- address
- photo

Implemented and tested separately:

- reader detection
- card presence
- ATR retrieval
- reader/card monitor events

No document in this repository should contain a real Citizen ID or cardholder personal data.
