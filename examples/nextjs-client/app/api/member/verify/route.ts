/**
 * PRODUCTION member verification route.
 *
 * Differences from the EXAMPLE_ONLY route at `/api/member-verification/id-card`:
 *
 * - Staff identity comes from an **authenticated session**, verified server-side. Client-supplied
 *   identity in a header, body, query parameter, or storage is ignored entirely.
 * - The member directory is resolved from configuration and fails closed when unconfigured, rather
 *   than falling back to mock data.
 *
 * The citizen ID is used for the lookup and then discarded: never persisted, never logged, never
 * returned to the browser.
 */

import { randomUUID } from "node:crypto";
import { NextResponse } from "next/server";
import { resolveMemberDirectory } from "@/lib/member-directory-config";
import {
  InMemoryVerificationAuditSink,
  statusForOutcome,
  verifyMember,
  type VerificationAuditSink,
} from "@/lib/member-verification";
import {
  SignedSessionStaffIdentityProvider,
  UnconfiguredStaffIdentityProvider,
  type StaffIdentityProvider,
} from "@/lib/staff-identity";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const noStoreHeaders = {
  "Cache-Control": "no-store, max-age=0",
  Pragma: "no-cache",
} as const;

// Replace with the cooperative's audit store. The in-memory sink is a placeholder that loses
// records on restart and is not acceptable for production audit retention.
const auditSink: VerificationAuditSink = new InMemoryVerificationAuditSink();

function createStaffIdentityProvider(): StaffIdentityProvider {
  const secret = process.env.STAFF_SESSION_SECRET;
  if (!secret) {
    // No secret means no way to verify a session, so nothing is authenticated. Falling back to a
    // default operator identity would silently forge the audit trail.
    return new UnconfiguredStaffIdentityProvider();
  }
  return new SignedSessionStaffIdentityProvider({
    secret,
    cookieName: process.env.STAFF_SESSION_COOKIE_NAME,
  });
}

export async function POST(request: Request) {
  const verificationId = randomUUID();

  // 1. Authenticate the operator BEFORE reading the body. An unauthenticated request must not be
  //    able to trigger a member lookup at all.
  const staffProvider = createStaffIdentityProvider();
  const staff = await staffProvider.resolve(request);
  if (!staff.authenticated) {
    return NextResponse.json(
      { error: "STAFF_NOT_AUTHENTICATED", reason: staff.reason, verificationId },
      { status: 401, headers: noStoreHeaders },
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

  let directory;
  try {
    directory = resolveMemberDirectory().directory;
  } catch {
    // A misconfigured directory (for example the mock enabled outside development) must not fall
    // back to something permissive.
    return NextResponse.json(
      { error: "MEMBER_DIRECTORY_MISCONFIGURED", verificationId },
      { status: 503, headers: noStoreHeaders },
    );
  }

  try {
    const result = await verifyMember({
      citizenId,
      verificationId,
      readerName: typeof readerName === "string" && readerName.length > 0 ? readerName : "unknown",
      // Identity comes from the verified session only. Any staff identifier in the request body or
      // headers is ignored — it is never read.
      staffIdentifier: staff.identity.staffId,
      workstationIdentifier: staff.identity.workstationId,
      department: staff.identity.department ?? null,
      directory,
      auditSink,
      correlationKey: process.env.MEMBER_VERIFICATION_CORRELATION_KEY,
    });

    return NextResponse.json(result, { status: statusForOutcome(result.outcome), headers: noStoreHeaders });
  } catch {
    // Never surface the underlying error: it could quote the request payload, the SQL, or bound
    // parameter values.
    return NextResponse.json(
      { error: "MEMBER_VERIFICATION_FAILED", verificationId },
      { status: 500, headers: noStoreHeaders },
    );
  }
}
