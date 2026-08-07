/**
 * ============================================================================================
 * EXAMPLE_ONLY_NOT_FOR_PRODUCTION
 * ============================================================================================
 *
 * Demonstration route for the Phase 13A verification flow. **Do not deploy this route.**
 *
 * Why it is not production-safe:
 *
 * 1. Staff identity is read from a request header with a placeholder fallback. That is
 *    client-controlled: any caller could attribute its action to any operator, making the audit
 *    trail worthless.
 * 2. It runs against the development mock directory, which returns fabricated member records.
 * 3. Its audit sink is in-memory and loses every record on restart.
 *
 * The production route is `POST /api/member/verify`
 * ([app/api/member/verify/route.ts](../../member/verify/route.ts)). It authenticates the operator
 * through a server-verified session, resolves the member directory from configuration, and fails
 * closed when either is missing.
 */

import { randomUUID } from "node:crypto";
import { NextResponse } from "next/server";
import { MockMemberDirectory } from "@/lib/member-directory-mock";
import {
  InMemoryVerificationAuditSink,
  statusForOutcome,
  verifyMember,
  type VerificationAuditSink,
} from "@/lib/member-verification";
import type { MemberDirectory } from "@/lib/member-directory";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

/** EXAMPLE_ONLY_NOT_FOR_PRODUCTION marker, also asserted by tests. */
export const EXAMPLE_ROUTE_CLASSIFICATION = "EXAMPLE_ONLY_NOT_FOR_PRODUCTION";

const auditSink: VerificationAuditSink = new InMemoryVerificationAuditSink();
const directory: MemberDirectory = new MockMemberDirectory();

const noStoreHeaders = {
  "Cache-Control": "no-store, max-age=0",
  Pragma: "no-cache",
  "X-Route-Classification": EXAMPLE_ROUTE_CLASSIFICATION,
} as const;

export async function POST(request: Request) {
  const verificationId = randomUUID();

  // Refuse to run outside development. This route trusts a client header for operator identity and
  // serves mock member data, so it must never be reachable in a deployed environment.
  if ((process.env.NODE_ENV ?? "development") !== "development") {
    return NextResponse.json(
      { error: EXAMPLE_ROUTE_CLASSIFICATION, verificationId },
      { status: 404, headers: noStoreHeaders },
    );
  }

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json(
      { error: "INVALID_REQUEST", verificationId },
      { status: 400, headers: noStoreHeaders },
    );
  }

  const { citizenId, readerName } = (body ?? {}) as { citizenId?: unknown; readerName?: unknown };
  if (typeof citizenId !== "string" || citizenId.length === 0) {
    return NextResponse.json(
      { error: "INVALID_REQUEST", verificationId },
      { status: 400, headers: noStoreHeaders },
    );
  }

  try {
    const result = await verifyMember({
      citizenId,
      verificationId,
      readerName: typeof readerName === "string" && readerName.length > 0 ? readerName : "unknown",
      // EXAMPLE_ONLY_NOT_FOR_PRODUCTION: client-controlled. The production route takes these from a
      // server-verified session instead.
      staffIdentifier: request.headers.get("x-staff-id") ?? "example-operator",
      workstationIdentifier: request.headers.get("x-workstation-id") ?? "example-workstation",
      directory,
      auditSink,
      correlationKey: process.env.MEMBER_VERIFICATION_CORRELATION_KEY,
    });

    return NextResponse.json(result, { status: statusForOutcome(result.outcome), headers: noStoreHeaders });
  } catch {
    return NextResponse.json(
      { error: "MEMBER_VERIFICATION_FAILED", verificationId },
      { status: 500, headers: noStoreHeaders },
    );
  }
}
