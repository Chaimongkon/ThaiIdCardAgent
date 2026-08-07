# Thai Card Data Provider

How ThaiIdCardAgent reads identity data from a physical Thai national ID card, and the rules that
govern which implementations are allowed to exist.

> **Status: BLOCKED_OFFICIAL_PROTOCOL_REQUIRED.**
> No official Department of Provincial Administration (DOPA) technical material is present in this
> repository, so no real provider has been implemented. `POST /api/v1/card/read` returns
> `THAI_CARD_PROTOCOL_NOT_CONFIGURED`. What ships today is the provider abstraction, the
> not-configured provider, the read orchestration, the API contract, the member verification flow,
> the UI, and the tests.

## Authorization requirement

A real provider may be built **only** from official technical material: an SDK, reader program,
protocol specification, header files, DLLs, sample code, or integration agreement issued by
Thailand's Department of Provincial Administration or supplied under an authorized agreement.

The following are **not** acceptable sources, regardless of how widely they are reproduced:

- Blog posts and tutorials
- Unofficial GitHub repositories and gists
- Stack Overflow answers and forum posts
- Undocumented third-party libraries that embed a command set
- Command sequences recovered by observing another application

This is not only a legal constraint. A command set inferred from an unofficial source carries no
guarantee about card generations, data encodings, or error semantics, so a card that behaves
differently from the one someone happened to test against can yield a wrong identifier — and a
wrong identifier attached to a member record is a materially worse outcome than a failed read.

**Until official material is available, do not implement APDU commands, and do not make
`/api/v1/card/read` return real data.**

## Architecture

```
POST /api/v1/card/read                         (Program.cs — HTTP, auth, audit)
        |
        v
ThaiCardIdentityReadService                    (preconditions, timeout, single-flight, validation)
        |
        v
IThaiCardDataProvider                          <-- the only place card commands may live
        |
        +-- NotConfiguredThaiCardDataProvider  (ships today; always fails closed)
        +-- OfficialDopaThaiCardDataProvider   (NOT IMPLEMENTED — requires official material)
        +-- MockThaiCardDataProvider           (tests only; never registered in the host)
```

The provider is isolated from every other concern. No APDU constant, command sequence, or decoding
rule may appear in:

- ASP.NET endpoints or `Program.cs`
- PC/SC reader enumeration (`ThaiIdCardAgent.Pcsc`)
- JWT authentication
- The SSE monitor
- The Next.js client

`CardReadEndpointTests.Host_RegistersTheNotConfiguredProvider_NeverTheMock` asserts the host wires
the not-configured provider, so a test double cannot reach production and return fabricated data.

## The contract

[`IThaiCardDataProvider`](../src/ThaiIdCardAgent.ThaiCard/ThaiCardDataProvider.cs):

```csharp
public interface IThaiCardDataProvider
{
    string ProviderName { get; }
    bool IsConfigured { get; }
    Task<ThaiIdCardIdentityResult> ReadCitizenIdAsync(ThaiCardReadContext context, CancellationToken cancellationToken = default);
}
```

`ThaiIdCardIdentityResult` carries the citizen ID and nothing else about the person:

| Field | Notes |
| --- | --- |
| `RequestId` | Correlates the read with the API request and the audit record |
| `ReaderName` | Reader the card was read from |
| `CitizenId` | 13 decimal digits, checksum validated. **Never logged** |
| `ReadAtUtc` | When the read completed |
| `ProviderName` | Which provider produced the value |
| `CardAtr` | Optional diagnostics only; not personal data; omitted unless requested |

There is **no field** for photo, address, name, birth date, religion, or issue/expiry dates. Phase
13A reads the citizen ID only, and the contract is shaped so nothing else can be returned by
accident.

## What the read service enforces

[`ThaiCardIdentityReadService`](../src/ThaiIdCardAgent.ThaiCard/ThaiCardIdentityReadService.cs)
sequences the checks around a read. It never issues a card command itself.

| Check | Behaviour |
| --- | --- |
| Provider configured | Fails closed with `THAI_CARD_PROTOCOL_NOT_CONFIGURED` before any hardware access |
| Single in-flight read | A concurrent read is **rejected** with `AGENT_BUSY`, not queued — queueing would let a double-click become two reads of the same card |
| Named reader exists | An unknown reader name is rejected; it never silently falls back to another reader, which could read a different person's card |
| Reader selection | Zero readers → `READER_NOT_FOUND`; multiple readers with no selection → `READER_SELECTION_REQUIRED` |
| Card present | Verified before any card command; `CARD_NOT_PRESENT` otherwise |
| Timeout | Configurable deadline (default 15s) → `CARD_READ_TIMEOUT` |
| Cancellation | Caller cancellation propagates as cancellation, never mislabelled as a timeout |
| Citizen ID validation | 13 digits + checksum. Failure → `CARD_DATA_INVALID`. **Never repaired** |

## Citizen ID validation

[`ThaiCitizenId`](../src/ThaiIdCardAgent.Core/ThaiCitizenId.cs) validates strictly:

- Exactly 13 ASCII decimal digits.
- Check digit: `(11 - (Σ digitᵢ × (13 − i)) mod 11) mod 10` over the first twelve digits.
- Separators are **not** stripped, whitespace is **not** trimmed into validity, and Thai or
  Arabic-Indic digit forms are **not** normalized.
- A wrong check digit is **never** corrected.

Repairing a malformed value would produce a different person's identifier, so malformed card data
fails the read. The rejection reason is an enum (`ThaiCitizenIdValidationResult`), so the rejected
value cannot leak through an error path.

The same algorithm is implemented in TypeScript in
[`lib/member-verification.ts`](../examples/nextjs-client/lib/member-verification.ts), and tests on
both sides assert they agree — otherwise one layer would accept what the other rejects.

## Error codes

| Code | HTTP | Meaning |
| --- | --- | --- |
| `THAI_CARD_PROTOCOL_NOT_CONFIGURED` | 501 | No authorized provider is configured |
| `READER_NOT_FOUND` | 404 | Named reader does not exist, or no readers at all |
| `READER_SELECTION_REQUIRED` | 422 | Multiple readers and no selection |
| `CARD_NOT_PRESENT` | 422 | No card in the reader |
| `CARD_READ_TIMEOUT` | 504 | Read exceeded its deadline |
| `CARD_COMMUNICATION_ERROR` | 502 | Card communication failed |
| `CARD_DATA_INVALID` | 422 | Card data failed validation; never repaired |
| `CARD_REMOVED_DURING_READ` | 409 | Card withdrawn mid-read |
| `PROVIDER_UNAVAILABLE` | 503 | Provider configured but device/SDK unavailable |
| `AGENT_BUSY` | 409 | Another read is already in flight |

Error responses are sanitized: no stack trace, no inner-exception text, no card content, no APDU
payload.

## Adding an official provider

When official material becomes available:

1. **Confirm authorization** in writing, and record the scope of what the agreement permits.
2. Implement `OfficialDopaThaiCardDataProvider : IThaiCardDataProvider` in
   `ThaiIdCardAgent.ThaiCard`. Card commands live there and nowhere else.
3. Read **only** the citizen ID. Do not implement photo, address, name, or birth date reads —
   Phase 13A does not need them and data minimization is a requirement, not a preference.
4. Map device and protocol failures onto the existing error codes. Never let a raw driver message
   or card response reach the caller.
5. Register it in `Program.cs` in place of `NotConfiguredThaiCardDataProvider`.
6. Add provider tests that use synthetic checksum-valid identifiers only.
7. Validate against real hardware through the Windows Service before enabling it anywhere.
8. Update this document, `IMPLEMENTATION-STATUS.md`, and `PRODUCTION-READINESS.md`.

**Never commit** a real citizen ID, real card data, a raw card dump, or an APDU trace containing
personal data — including in tests, fixtures, logs, or screenshots.

## Testing without hardware

[`MockThaiCardDataProvider`](../src/ThaiIdCardAgent.ThaiCard/Testing/MockThaiCardDataProvider.cs)
is a scriptable test double: it can return a value, throw a specific failure, hang to exercise the
timeout, or run custom behaviour for concurrency tests. It contains **no card protocol** and
cannot read a physical card.

It must never be registered in the service host. Every identifier used with it is synthetic and
checksum-valid.

## Related

- [MEMBER-IDENTITY-VERIFICATION.md](MEMBER-IDENTITY-VERIFICATION.md) — the cooperative member flow.
- [SECURITY-BOUNDARIES.md](SECURITY-BOUNDARIES.md) — trust boundaries and what never crosses them.
- [API.md](API.md) — endpoint reference.
- [PRODUCTION-READINESS.md](PRODUCTION-READINESS.md) — readiness checklist.
