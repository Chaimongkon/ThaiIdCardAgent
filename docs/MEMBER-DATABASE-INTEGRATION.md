# Cooperative Member Database Integration

How the verification flow reaches the cooperative member database, what must still be supplied
before it can run against the real system, and why the schema is configuration rather than code.

> **Status: SCHEMA REQUIRED.** No table name, column name, package name, connection string, or
> database engine has been supplied, and none is invented here. The seam, validation, error
> handling, staff authentication, mock dataset, and tests are complete; the lookup runs against the
> development mock until the schema values in
> [`config/member-database.config.template.json`](../examples/nextjs-client/config/member-database.config.template.json)
> are filled in. See "Information still required" below.

## Architecture

```
POST /api/member/verify                    production route
  |  1. authenticate the operator          StaffIdentityProvider (server-verified session)
  |  2. validate the citizen ID            13 digits + checksum, before any lookup
  v
verifyMember()                             orchestration; holds the citizen ID only for the lookup
  |
  v
MemberDirectory                            <-- the only seam that touches the member database
  +-- MockMemberDirectory                  development only; synthetic members
  +-- SqlMemberDirectory                   operator-supplied statement + injected driver
  +-- NotConfiguredMemberDirectory         default; reports MEMBER_DB_NOT_CONFIGURED
        |
        v
MemberDatabaseClient                       implemented by the host app with its real driver
```

### Why the SQL is configuration

Writing a query requires knowing the schema. Rather than invent table and column names that would
have to be rewritten, `SqlMemberDirectory` takes the lookup statement and the result-column mapping
from configuration supplied by the cooperative's DBA, and refuses to run while any required value is
still an unresolved `<PLACEHOLDER>`.

### Why the driver is injected

The database engine has not been confirmed either, so nothing in this repository depends on an
Oracle, SQL Server, or PostgreSQL package. The host application implements `MemberDatabaseClient`
with whatever driver it actually uses:

```ts
export interface MemberDatabaseClient {
  query(
    sql: string,
    parameters: Readonly<Record<string, string>>,
    options: { timeoutMs: number; signal?: AbortSignal },
  ): Promise<MemberDatabaseRow[]>;
}
```

The citizen ID is always passed as a **bind parameter**, never interpolated into the statement. An
implementation must not log parameter values — they carry the citizen ID.

## Lookup outcomes

`MemberDirectory.lookupByCitizenId` returns exactly one of four results. There is no "maybe".

| Result | Meaning | HTTP |
| --- | --- | --- |
| `Found` | Exactly one row matched | 200 |
| `NotFound` | Zero rows matched | 200 |
| `Duplicate` | More than one row matched — **fails closed** | 409 |
| `DatabaseUnavailable` | Timeout, connection failure, query failure, unconfigured, or an unusable row | 503 |

### Duplicates fail closed

Two members sharing a citizen ID is a data-integrity fault in the member database. `SqlMemberDirectory`
**never inspects the rows and never selects the first one** — picking either could attach the wrong
person to a transaction, and there is no basis for preferring one over the other. The result carries
only a match count; no candidate member data is returned, logged, or shown.

The lookup statement must therefore **not** apply its own row limit (`ROWNUM = 1`, `FETCH FIRST 1 ROW`,
`TOP 1`, `LIMIT 1`). A limit would silently reduce a duplicate to a single row and defeat this check.

## What a member record may contain

```ts
type MemberRecord = {
  memberId: string;            // internal member ID
  memberNo: string;            // member number
  fullName: string;
  memberType: string | null;
  memberStatus: string | null;
  photoReference: string | null;
};
```

There is deliberately **no `citizenId` field**. The citizen ID goes in as a lookup key and does not
come back out: once matched, the system holds member identity, not the national identifier.

`photoReference` is an identifier for a photo the cooperative system already holds — a URL, key, or
id. It is **never image bytes**. A driver returning a BLOB in that column is rejected and mapped to
`null`, so photo bytes cannot be serialized onward to the browser. The UI displays the reference as
text and renders no `<img>` from it.

Member status is **reported, not filtered**. An inactive, resigned, or suspended member still matches
and is returned with their status, so staff can see the situation and act on it. Suppressing the
record would hide the fact that the person is a member at all.

## Error sanitization

Database failures are mapped to a small set of codes. Driver messages, SQL text, connection strings,
and bound parameter values never escape `SqlMemberDirectory`, because any of them may quote the
citizen ID or a credential — Oracle and ODBC errors routinely echo the statement and its parameters.

| Code | Cause |
| --- | --- |
| `MEMBER_DB_NOT_CONFIGURED` | No mapping and/or client configured |
| `MEMBER_DB_TIMEOUT` | Query deadline elapsed, or the request was aborted |
| `MEMBER_DB_UNAVAILABLE` | Connection refused/reset, host unreachable, DNS failure |
| `MEMBER_DB_QUERY_FAILED` | Any other driver or query error |
| `MEMBER_DB_ROW_INVALID` | A matched row is missing a required identity field |

An unconfigured directory reports `DatabaseUnavailable`, **never** `NotFound`: a deployment that has
not been wired up must not look like a genuine miss.

## Statement validation

`validateMemberDatabaseMapping` rejects, before any query runs:

- An unresolved `<PLACEHOLDER>` in the statement, the bind parameter, or a required column.
- A statement that is not a `SELECT` / `WITH`.
- More than one statement (a semicolon anywhere but the end).
- Any write keyword (`insert`, `update`, `delete`, `merge`, `drop`, `truncate`, `alter`, `create`,
  `grant`, `revoke`, `execute`, `exec`, `call`).
- A statement that does not reference the declared bind parameter.
- A literal 13-digit run in the statement — someone pasted an identifier instead of binding it.

This is **defense in depth, not a SQL parser**. It cannot prove an arbitrary statement is safe. Its
job is to catch the mistakes that actually happen. The statement still comes from a trusted DBA and
should be reviewed as production code.

## Staff identity

The operator recorded in an audit record must be the operator who is actually signed in.

Staff identity is **never** taken from a request header, request body, query parameter,
`localStorage`, or `sessionStorage`. Every one of those is chosen by the client, so a client could
attribute its action to somebody else and the audit trail would be worthless.

The production route resolves identity through `StaffIdentityProvider`:

| Provider | Behaviour |
| --- | --- |
| `SignedSessionStaffIdentityProvider` | Verifies an HMAC-SHA256 signed session cookie. The client transmits it but cannot forge one, because the signature requires a server-held secret. |
| `UnconfiguredStaffIdentityProvider` | Default. Authenticates nobody. A deployment with no staff auth wired rejects every request rather than falling back to a placeholder operator. |

`SignedSessionStaffIdentityProvider` is a self-contained reference implementation. A deployment with
existing SSO should implement `StaffIdentityProvider` against that system instead; nothing else in
the flow changes.

Configure `STAFF_SESSION_SECRET` server-side only. It must never be a `NEXT_PUBLIC_` variable, never
reach the browser, and never be committed.

## Routes

| Route | Classification |
| --- | --- |
| `POST /api/member/verify` | **Production.** Authenticated staff session, configured directory, fails closed on both. |
| `POST /api/member-verification/id-card` | **`EXAMPLE_ONLY_NOT_FOR_PRODUCTION`.** Trusts a client header for operator identity, serves mock data, in-memory audit. Returns 404 outside development and sets `X-Route-Classification: EXAMPLE_ONLY_NOT_FOR_PRODUCTION`. |

## Development mock

`MEMBER_DIRECTORY_USE_MOCK=true` selects `MockMemberDirectory`. `resolveMemberDirectory` **throws**
if this is set outside development — serving fabricated member records to real traffic would let a
deployment verify people against invented data.

Synthetic, checksum-valid citizen IDs, all patterned so they cannot be mistaken for real ones:

| Constant | Value | Scenario |
| --- | --- | --- |
| `activeMember` | `1000000000009` | Active ordinary member |
| `activeMemberWithPhoto` | `2123456789012` | Active associate member with a photo reference |
| `inactiveMember` | `5999000111229` | Resigned member — matches, status surfaced |
| `suspendedMember` | `1111111111119` | Suspended member |
| `duplicatedMember` | `3100600445716` | Present in two rows — exercises the fail-closed path |
| `unknownMember` | `1101700207366` | Valid checksum, in no member record |

Mock member records are prefixed `MOCK-` and named `ทดสอบ …`. **Never put real member data here.**

### Manual verification page (development only)

While the card provider is blocked there is no way to read a real card, so a manual harness stands
in for the card read:

```
http://localhost:3000/dev/member-verification
```

Available only under `npm run dev`. The environment gate lives in a **server component**, so outside
development the route resolves to `notFound()` (HTTP 404) and the panel's client bundle is never
sent. Verified at runtime: `next start` returns 404, `next dev` returns 200.

The page shows a permanent banner — *ข้อมูลจำลองสำหรับการพัฒนา — ไม่ได้อ่านจากบัตรจริง* — offers the four
scenarios above, posts to the `EXAMPLE_ONLY_NOT_FOR_PRODUCTION` route, and renders the Member Card
with the masked citizen ID only. A **จำลองการถอดบัตร (CardRemoved)** button clears the card the way the
real SSE event does.

It separates **identity matching** from **transaction eligibility**: the card states plainly that the
system does not decide eligibility, and a member whose status is not `ปกติ` is flagged as
matched-but-restricted. Reading "identity matched" as "cleared to transact" is the wrong inference
for a resigned or suspended member.

The citizen ID is never held in component state — the picker stores a scenario key and the
identifier is read from a module constant inside the submit handler — so it cannot reach a URL,
`localStorage`, `sessionStorage`, the console, or a React DevTools state dump.

## Configuration reference

| Variable | Purpose |
| --- | --- |
| `MEMBER_DIRECTORY_USE_MOCK` | `true` selects the mock. Development only. |
| `MEMBER_DB_LOOKUP_SQL` | Lookup statement confirmed by the DBA |
| `MEMBER_DB_CITIZEN_ID_PARAMETER` | Bind parameter name as used in the statement |
| `MEMBER_DB_COLUMN_MEMBER_ID` | Result column for the internal member ID |
| `MEMBER_DB_COLUMN_MEMBER_NO` | Result column for the member number |
| `MEMBER_DB_COLUMN_FULL_NAME` | Result column for the full name |
| `MEMBER_DB_COLUMN_MEMBER_TYPE` | Optional |
| `MEMBER_DB_COLUMN_MEMBER_STATUS` | Optional |
| `MEMBER_DB_COLUMN_PHOTO_REFERENCE` | Optional; must be a reference, not a BLOB |
| `MEMBER_DB_QUERY_TIMEOUT_MS` | Query deadline, default 5000 |
| `STAFF_SESSION_SECRET` | HMAC secret for the staff session. Server-side only. |
| `STAFF_SESSION_COOKIE_NAME` | Default `coop_staff_session` |
| `MEMBER_VERIFICATION_CORRELATION_KEY` | Keyed HMAC for audit correlation |

## Information still required

The integration cannot run against the real database until these are supplied. **Nothing below is
guessed.**

1. **Database engine and version** — Oracle, SQL Server, PostgreSQL, or other. Determines which
   driver implements `MemberDatabaseClient` and the bind-parameter syntax.
2. **The lookup statement**, written or approved by the cooperative's DBA, satisfying the rules
   above (single read-only SELECT, exact equality on a bound citizen ID, no row limit, citizen ID
   not selected into the result).
3. **Schema and table name** holding member records.
4. **Column name storing the citizen ID**, and confirmation of its stored format: is it exactly 13
   digits, or does it carry separators, padding, or a check-digit column? An exact match against a
   differently-formatted column silently returns `NotFound` for everyone.
5. **Result column names** for internal member ID, member number, full name, member type, member
   status, and photo reference.
6. **Whether a photo reference exists at all**, and if so whether it is a URL, a key, or a foreign
   key into another table. If photos are stored as BLOBs, confirm how a reference is derived —
   photo bytes must not flow through this path.
7. **Whether an Oracle package or stored procedure** is the sanctioned access path instead of a
   direct SELECT. If so, `MemberDatabaseClient` calls it and the mapping describes its result set.
8. **Connection ownership** — who provisions the connection, the account, and its privileges. The
   account should have SELECT on the member view only.
9. **Duplicate handling policy** — is a duplicate citizen ID possible in the live data today, and
   what is the operational procedure when one is found?
10. **Member status and type vocabularies** — the set of values and which of them should block a
    counter transaction. The system currently reports status without interpreting it.
11. **Staff authentication system** — how operators sign in, so `StaffIdentityProvider` can be
    implemented against it rather than the reference signed-cookie provider.
12. **Audit destination and retention** — where verification audit records must be written and for
    how long they must be kept.

## Related

- [MEMBER-IDENTITY-VERIFICATION.md](MEMBER-IDENTITY-VERIFICATION.md) — the end-to-end flow.
- [THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md) — card reading (still `BLOCKED_OFFICIAL_PROTOCOL_REQUIRED`).
- [SECURITY-BOUNDARIES.md](SECURITY-BOUNDARIES.md) — trust boundaries.
