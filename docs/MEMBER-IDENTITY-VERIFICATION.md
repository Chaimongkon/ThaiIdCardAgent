# Cooperative Member Identity Verification

Phase 13A flow: an operator reads a member's Thai ID card at the counter, and the system matches the
citizen ID against the cooperative member database to confirm who is standing there.

> **Status: BLOCKED_OFFICIAL_PROTOCOL_REQUIRED.** The card-reading step requires an authorized
> provider that does not yet exist (see [THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md)). The full
> flow, contracts, mock repository, UI, and tests are implemented and exercised end to end against a
> mock provider. `POST /api/v1/card/read` returns `THAI_CARD_PROTOCOL_NOT_CONFIGURED` until then.

## Purpose and scope

This is **preliminary identity verification**: it establishes that the person at the counter holds
a card whose citizen ID matches a member record. It is not proof of identity on its own — it does
not verify that the cardholder is the person pictured, and it performs no biometric check. Treat it
as one factor in a staff-supervised process.

Phase 13A reads the **13-digit citizen ID only**. Photo, address, name, birth date, and religion are
out of scope and are not read.

## Flow

```
Browser (staff workstation)
  |
  |  1. POST /api/local-agent/token            (Next.js server issues a fresh 60s JWT)
  |
  |  2. POST https://localhost:18443/api/v1/card/read
  |         Authorization: Bearer <fresh JWT with card.read>
  |     -> { citizenId, readerName, verificationId, readAtUtc, providerName }
  |        citizenId lives only in a local variable in the browser
  |
  |  3. POST /api/member-verification/id-card  (same-origin, server-side)
  |         { citizenId, readerName }
  |
  v
Next.js server
  |  4. validate citizen ID (13 digits + checksum) BEFORE any lookup
  |  5. match against the cooperative member database
  |  6. write an audit record (masked ID + keyed correlation hash; never the raw ID)
  |
  v
  7. -> { verified, outcome, memberId, memberNo, fullName, maskedCitizenId, verificationId }
        no citizenId field exists in this response
```

The citizen ID crosses exactly two hops — agent to browser, browser to server — and is discarded
after the lookup. It is never persisted by the agent, the Next.js server, or the browser.

## Agent endpoint

`POST /api/v1/card/read` — see [API.md](API.md) and
[THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md).

Requirements enforced:

| Requirement | How |
| --- | --- |
| Explicit user action only | No polling or background variant exists |
| Fresh JWT per request | 60s maximum lifetime; `jti` replay cache rejects reuse |
| `card.read` permission | Authorization policy on the endpoint; a token without it gets 403 |
| Replay protection | Unchanged from the existing JWT handler |
| Reader and card verified | Before any card command |
| Timeout and cancellation | Configurable deadline; caller cancellation honoured |
| `Cache-Control: no-store` | Set on the response |
| No automatic retry | A failed read must be re-triggered by an explicit action |
| Sanitized errors | Structured codes, no stack traces, no card content |

### Permission claim

The token must carry `card.read` in a `scope` claim (space-delimited) or a `permissions` claim:

```json
{ "sub": "operator-1", "workstation_id": "counter-1", "scope": "card.status card.read", "jti": "…" }
```

In Development the development key carries every permission; it is rejected outright outside
Development, so this cannot widen production access.

## Verification route

`POST /api/member-verification/id-card` in
[`examples/nextjs-client`](../examples/nextjs-client/app/api/member-verification/id-card/route.ts).

Request: `{ "citizenId": "<13 digits>", "readerName": "Reader A" }`

Response:

```json
{
  "verified": true,
  "outcome": "MEMBER_MATCHED",
  "verificationId": "…",
  "memberId": "M-0001",
  "memberNo": "00001",
  "fullName": "…",
  "memberType": "สามัญ",
  "memberStatus": "ปกติ",
  "photoReference": "coop-photo/00001",
  "maskedCitizenId": "1-1017-xxxxx-36-6",
  "verifiedAtUtc": "2026-08-06T00:00:02Z"
}
```

There is deliberately **no `citizenId` field**. After verification the browser holds member identity,
not the national identifier.

`photoReference` is an identifier for a photo the cooperative system already holds — never image
bytes, and never the card photo (which is not read at all). Member status is reported rather than
filtered: an inactive or suspended member still matches and is returned with their status.

| Outcome | HTTP | Meaning |
| --- | --- | --- |
| `MEMBER_MATCHED` | 200 | Exactly one member matched |
| `MEMBER_NOT_FOUND` | 200 | No member matched |
| `MEMBER_DUPLICATE` | 409 | More than one member carries this citizen ID — **fails closed** |
| `MEMBER_DATABASE_UNAVAILABLE` | 503 | Member database unreachable, timed out, or unconfigured |
| `CITIZEN_ID_INVALID` | 422 | Failed validation; rejected before any lookup |

**Why duplicates fail closed:** two members sharing a citizen ID is a data-integrity fault in the
member database. Picking one could attach the wrong person to a transaction, so the system refuses,
never inspects the candidate rows, and requires manual resolution.

### Routes

| Route | Classification |
| --- | --- |
| `POST /api/member/verify` | **Production.** Authenticated staff session; configured member directory; fails closed on both. |
| `POST /api/member-verification/id-card` | **`EXAMPLE_ONLY_NOT_FOR_PRODUCTION`.** Trusts a client header for operator identity and serves mock data. Returns 404 outside development. |

### Connecting the real database

See [MEMBER-DATABASE-INTEGRATION.md](MEMBER-DATABASE-INTEGRATION.md) for the full architecture and
the list of schema information still required. In brief, the route depends on:

```ts
export interface MemberDirectory {
  lookupByCitizenId(citizenId: string): Promise<MemberLookupResult>;
}
```

`SqlMemberDirectory` implements it against an operator-supplied lookup statement and an injected
driver, so no table name, column name, or connection string is defined in this repository.
`MockMemberDirectory` (synthetic data, development only) ships today.

**Staff identity comes from a server-verified session, never from the client.** `staffIdentifier`
and `workstationIdentifier` are resolved through `StaffIdentityProvider`; a header, body field,
query parameter, or storage value is never read. The example route is the exception and is marked
accordingly.

## Web UI

Button: **ตรวจสอบตัวตนด้วยบัตรประชาชน**

| Behaviour | Implementation |
| --- | --- |
| Disabled when the agent has not been reached | `health === null` |
| Disabled when no reader | `selectedReader` empty |
| Disabled when no card | `cardState !== "CardPresent"` |
| Explicit click required | No effect on render, mount, or reader refresh |
| Progress shown while reading | Button label changes; `role="status"` region |
| Double submission prevented | Guarded on a ref, not state — two clicks in one tick would both see stale state — plus the button disables itself |
| Member data shown only after a database match | Rendered only for `MEMBER_MATCHED` |
| Masked citizen ID shown | `1-1017-xxxxx-36-6` |
| `MEMBER_NOT_FOUND` shown safely | Message only; no member fields, no identifier |
| Sensitive state cleared on `CardRemoved` | SSE handler clears the member record |
| No citizen ID in the URL | Sent in the POST body only |
| No `localStorage` / `sessionStorage` | Nothing is written; tests assert both are empty |
| No citizen ID in the console | Tests assert every console call is free of it |

The existing **Card Read** diagnostic button remains separate and stays disabled.

## Audit

Every attempt produces an audit record on both sides.

Agent side ([`IdentityVerificationAuditRecord`](../src/ThaiIdCardAgent.Core/IdentityVerificationAudit.cs)):

| Field | Notes |
| --- | --- |
| `VerificationId` | Also returned to the caller |
| `TimestampUtc` | |
| `StaffIdentifier` | JWT subject |
| `WorkstationIdentifier` | JWT `workstation_id` |
| `ReaderName` | |
| `Outcome` | `CardReadSucceeded` / `CardReadFailed` / member outcomes |
| `MemberId` | Only when exactly one member matched |
| `ErrorCode` | Sanitized code only |
| `MaskedCitizenId` | For operator display |
| `CitizenIdCorrelationHash` | Keyed HMAC; only when correlation is configured |
| `ProviderName` | |

The record type has **no field** capable of holding a raw citizen ID, photo, address, APDU trace,
JWT, or key material. The type system is what keeps personal data out of the audit trail, not care
at each call site.

### Why correlation is a keyed HMAC

A citizen ID has roughly 10¹² valid values, so an unkeyed hash of one can be reversed by exhaustive
search in seconds — it would be a reversible encoding of the identifier, not a protection. Keying
with a secret the attacker does not hold is what makes the output non-linkable.

Configure the key out-of-band:

- Agent: `Security:CitizenIdCorrelationKey` (or `Security__CitizenIdCorrelationKey`)
- Next.js: `MEMBER_VERIFICATION_CORRELATION_KEY`

When no key is configured, **no hash is produced** and the audit record simply carries no
correlation value, rather than a weak one. Never commit the key.

## Data minimization

| Data | Read? | Stored? |
| --- | --- | --- |
| Citizen ID (13 digits) | Yes | No — in memory for the lookup, then discarded |
| Masked citizen ID | Derived | Yes, in audit records and on screen |
| Keyed correlation hash | Derived | Only when a key is configured |
| Photo | **No** | No |
| Address | **No** | No |
| Name from the card | **No** | No |
| Birth date, religion | **No** | No |
| Member name | From the cooperative database, not the card | Per existing database policy |

Retaining the raw citizen ID after verification is out of scope for Phase 13A. If the cooperative's
own database already holds it under an existing legal basis, that is a separate system with its own
retention rules; this flow does not add a new copy.

## Remaining approvals

Phase 13A is technically complete but **not deployable**. Still outstanding:

1. **Official DOPA technical material** and written authorization to read cards.
2. **Legal basis** for reading and processing citizen IDs at the counter, under Thailand's PDPA:
   lawful basis, retention period, data-subject notice, and the cooperative's privacy notice.
3. **Member database schema and connection details.** The seam, validation, error handling, and
   tests are complete, but the lookup statement, schema, table, and column names have not been
   supplied — see "Information still required" in
   [MEMBER-DATABASE-INTEGRATION.md](MEMBER-DATABASE-INTEGRATION.md).
4. **Staff authentication system** so `StaffIdentityProvider` is implemented against the real
   sign-in flow rather than the reference signed-cookie provider.
5. **Audit storage** with a defined retention period and access control. The current sink is
   in-memory and loses records on restart.
6. **Real hardware validation** through the Windows Service.
7. **Security review** with no Critical or High findings.

## Related

- [THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md) — provider architecture and authorization rules.
- [SECURITY-BOUNDARIES.md](SECURITY-BOUNDARIES.md) — trust boundaries.
- [WEB-INTEGRATION.md](WEB-INTEGRATION.md) — browser-to-agent integration.
- [API.md](API.md) — endpoint reference.
