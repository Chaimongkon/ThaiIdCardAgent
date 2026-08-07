# Implementation Status

## Implemented

- .NET 10 solution and project structure.
- Core models, interfaces, result model, exceptions, error mapping, and PII redaction.
- Windows PC/SC reader listing, card presence, ATR, and polling monitor.
- Bitwise PC/SC state mapping for combined flags.
- Minimal API with authenticated local smart card endpoints.
- Development key authentication from configuration/user secrets/environment.
- Production JWT validation with issuer, audience, lifetime, required claims, and replay detection.
- Acceptance tooling that issues a fresh JWT for every Production API request.
- Authenticated SSE `/api/v1/events` with disconnect cleanup.
- Exact-origin CORS.
- Windows Service hosting configuration.
- Publish/install/uninstall/certificate scripts with `-WhatIf` support where applicable.
- Production `--diagnostics` command that checks configuration without opening a listener.
- Runnable Next.js integration example with server-side JWT broker, typed Agent client, fetch-streaming SSE, and UI tests.
- Secure release packaging and code-signing readiness (Phase 11): reproducible package, SHA-256 manifest, `release-manifest.json`, Authenticode signing pipeline, install-time integrity + rollback.
- Clean-machine pilot deployment acceptance tooling (Phase 12): `scripts/Test-PilotDeployment.ps1` (Full / VerifyOnly / Tamper / Rollback, `-WhatIf`) and read-only sanitized `scripts/Get-AgentDiagnostics.ps1` (JSON export), driven from a release ZIP with no source tree.
- Phase 13A identity-reading architecture: `IThaiCardDataProvider` seam isolated from endpoints/PC-SC/JWT/SSE/UI, `NotConfiguredThaiCardDataProvider` (fails closed), read orchestration with reader/card preconditions, timeout, cancellation and single-flight double-read protection, strict citizen ID checksum validation that never repairs data, `card.read` permission on `POST /api/v1/card/read`, sanitized error codes, and a PII-free audit contract with keyed correlation hashing. **No card protocol is implemented** — see Blocked.
- Cooperative member verification (Phase 13A): `MemberDirectory` seam with Found/NotFound/Duplicate/DatabaseUnavailable outcomes, masked citizen ID, audit records, and the `ตรวจสอบตัวตนด้วยบัตรประชาชน` UI flow showing member id/number/name/type/status/photo reference.
- Member database integration seam: `SqlMemberDirectory` driven by an operator-supplied lookup statement and an injected driver (no table, column, package, or connection string is defined in this repository), statement validation, sanitized database error codes, `NotConfiguredMemberDirectory` default, and a development-only mock dataset of synthetic checksum-valid members. **Schema values still required** — see Blocked.
- Authenticated staff identity: production `POST /api/member/verify` resolves the operator through a server-verified session and never reads staff identity from a header, body, query parameter, or storage. The example route is marked `EXAMPLE_ONLY_NOT_FOR_PRODUCTION` and returns 404 outside development.
- Development-only manual verification harness at `/dev/member-verification`: server-component environment gate (404 in production, verified at runtime with `next start`), mock-data banner, the four mock scenarios, Member Card with masked citizen ID, simulated CardRemoved, and an explicit separation of identity matching from transaction eligibility.
- Production signing tooling and policy (Production Signing workstream, phase S1): signing allowlist, placeholder-only signing configuration, stage-ordered `scripts/Invoke-ReleaseBuild.ps1` (publish → sign → verify → checksums → manifest → ZIP → verify package), signtool backend with RFC 3161 timestamping, embedded-signature verification, expanded manifest signing evidence, and the procurement/custody/workflow/acceptance documents. **Verified with test certificates only** — see Not Tested.

## Tested Without Hardware

- `dotnet clean -m:1 /nr:false`: passed.
- `dotnet restore -m:1 /nr:false`: passed.
- `dotnet build -c Release -m:1 /nr:false --no-restore`: passed with `0 Warning(s), 0 Error(s)` in the previous verification run.
- `dotnet test -c Release -m:1 /nr:false --filter "Category!=Hardware"`: passed with 209/209 in the previous verification run (Core 36, Pcsc 18, Service 87, Release 68).
- Next.js example: lint, typecheck, 132/132 vitest tests, and production build passed locally.
- `/dev/member-verification` verified at runtime: HTTP 200 under `next dev`, HTTP 404 under `next start`, and all four mock scenarios returned the expected outcome with no raw citizen ID in any response.
- win-x64 publish produced `artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe` in the previous verification run.
- PowerShell scripts parse under Windows PowerShell 5.1.
- Next.js example: lint, typecheck, unit tests, and production build passed locally after Phase 10 implementation.

## Tested With Hardware

Validated with a real PC/SC reader/card on the test machine:

- Reader detection: passed.
- Card absent state: passed.
- Card present state: passed.
- ATR: passed.
- Console status while card removed: `connected=True`, `cardPresent=False`, no ATR.
- Console status after reinsertion: `connected=True`, `cardPresent=True`, ATR present.

No Citizen ID, owner name, address, birth date, or photo has been read or documented.

## Production Acceptance Through Windows Service

Production Acceptance passed on the test machine:

- Windows Service installed and running.
- Service account: `NT AUTHORITY\LocalService`.
- PC/SC under service account: passed.
- HTTPS health without certificate-validation bypass: passed.
- JWT key preflight and runtime issue: passed.
- Readers API through service: passed.
- Card status API through service: passed.
- Card ATR API through service: passed.
- CardRemoved through status polling: passed after `NoCard` appeared 2 consecutive times.
- CardInserted through status polling: passed after `CardPresent` appeared 2 consecutive times.
- SSE CardRemoved through `/api/v1/events`: passed under Windows Service with real hardware.
- SSE CardInserted through `/api/v1/events`: passed under Windows Service with real hardware.
- SSE disconnect and reconnect repeated rounds: passed.
- Restart service health/readers: passed.
- Windows reboot and Automatic Delayed Start: passed.
- Upgrade: passed.
- Uninstall preserving config/logs: passed.
- Reinstall: passed.
- Certificate retention: passed.

## Not Tested

- Phase 10 browser manual acceptance of the new `examples/nextjs-client` UI against the installed Windows Service and real hardware.
- Production code signing with a real certificate. The signing pipeline is implemented and its gates are covered by tests, but every test uses an ephemeral self-signed certificate in `Cert:\CurrentUser\My`. Not exercised: a hardware token PIN prompt, an HSM CSP/KSP, a real CA chain, a real RFC 3161 timestamp service, and SmartScreen behaviour.

## Blocked

**`BLOCKED_OFFICIAL_PROTOCOL_REQUIRED` — Thai card data provider.**

No official Department of Provincial Administration SDK, reader program, protocol document, DLL,
header, example, or integration material is present in this repository, and none was supplied. Per
the Phase 13A restriction, no APDU command set may be guessed, invented, or copied from blogs,
unofficial repositories, forum posts, or undocumented third-party libraries.

Phase 13A therefore implemented only the provider abstraction, the not-configured provider, the
mock provider (tests only), the contracts, the API, the member verification flow, the UI, and the
tests. `POST /api/v1/card/read` returns `THAI_CARD_PROTOCOL_NOT_CONFIGURED` and must not return real
data until an authorized provider has been integrated and validated with real hardware.

To unblock: obtain official technical material and written authorization, then follow "Adding an
official provider" in [THAI-CARD-PROVIDER.md](THAI-CARD-PROVIDER.md).

**Member database schema required.**

No database engine, schema, table name, column name, package name, or connection detail has been
supplied, and none is invented. The `MemberDirectory` seam, `SqlMemberDirectory`, statement
validation, sanitized error mapping, staff authentication, mock dataset, and tests are complete; the
lookup runs against the development mock and reports `MEMBER_DB_NOT_CONFIGURED` in production.

To unblock: supply the twelve items in "Information still required" in
[MEMBER-DATABASE-INTEGRATION.md](MEMBER-DATABASE-INTEGRATION.md) — most importantly the database
engine, the DBA-approved lookup statement, the citizen ID column and **its stored format**, and the
result column names.

## Production Signing Workstream

**Status: NOT COMPLETE — DEFERRED.** Tooling, policy, tests, and checklists are done (phase S1).
Procurement, signing-workstation setup, and the real token/HSM acceptance run are **deferred by
decision**: the OV/EV choice, certificate purchase, hardware token/HSM, real RFC 3161 timestamping,
a signed production package, and clean-machine signed acceptance are all on hold. The pipeline is
retained and can be picked up again without rework.

Production Signing must not be claimed complete until a real code signing certificate and hardware
token/HSM have been obtained and a real timestamped signed release passes clean-machine acceptance.
Phase 13A proceeded ahead of this workstream by decision.

See [PRODUCTION-SIGNING-PLAN.md](PRODUCTION-SIGNING-PLAN.md) for phases and exit criteria,
[PRODUCTION-SIGNING-REQUIREMENTS.md](PRODUCTION-SIGNING-REQUIREMENTS.md) for procurement,
[SIGNING-KEY-CUSTODY-POLICY.md](SIGNING-KEY-CUSTODY-POLICY.md) for key custody,
[RELEASE-SIGNING-WORKFLOW.md](RELEASE-SIGNING-WORKFLOW.md) for the procedure, and
[PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md](PRODUCTION-SIGNING-ACCEPTANCE-CHECKLIST.md) for the
acceptance run.

## Not Implemented

- Citizen ID reading from a physical card. The abstraction, orchestration, validation, API, audit,
  and UI exist; the card protocol itself does not (`BLOCKED_OFFICIAL_PROTOCOL_REQUIRED`).
- Thai Card APDU provider.
- Cardholder name reading — **out of scope for Phase 13A**, deliberately not implemented.
- Birth date reading — **out of scope for Phase 13A**.
- Address reading — **out of scope for Phase 13A**.
- Photo reading — **out of scope for Phase 13A**.
- Connection to the live cooperative member database. The `MemberDirectory` seam,
  `SqlMemberDirectory`, and `MemberDatabaseClient` driver seam are complete; the schema values and
  database driver have not been supplied, so the flow runs against `MockMemberDirectory`.
- A `StaffIdentityProvider` implementation against the cooperative's real sign-in system. The
  production route works today with the reference signed-session provider.
- Audit persistence, retention policy, and access control. The current sink is in-memory.
- Production Authenticode signing of released binaries: the pipeline is implemented, but no release has been signed with a real organizational certificate on a hardware token/HSM.

## Security Limitations

- Production deployments must configure public JWT verification material and exact allowed origins.
- Private signing keys, JWTs, passwords, PFX/P12 files, and machine-specific secrets must not be stored in Git or logs. Signing PINs and passwords are entered interactively and are rejected by the tooling if placed in configuration or command-line arguments.
- Development key authentication is disabled outside Development environment.
- The local API is designed for loopback binding only.
- Released binaries are not yet signed with a production certificate, so SmartScreen and "unknown publisher" warnings still apply. Pilot builds remain `UnsignedPilot` and must not be distributed publicly.
- Card identity reading requires the `card.read` permission in the JWT, not merely authentication.
- No real citizen ID has been read. Every citizen ID in this repository is synthetic and checksum-valid.
- The raw citizen ID is never logged, never stored in the browser, and never returned by the member
  verification route. Audit records carry a masked form plus an optional keyed HMAC correlation
  hash; with no key configured, no hash is produced rather than a reversible one.
- Phase 13A identity verification is **preliminary**: it confirms the presented card's citizen ID
  matches a member record. It does not verify that the bearer is the cardholder and performs no
  biometric check, so it must remain part of a staff-supervised process.
- Legal basis for processing citizen IDs under Thailand's PDPA — lawful basis, retention period, and
  data-subject notice — is not yet established and is required before production use.
