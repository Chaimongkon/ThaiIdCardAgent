import { NextResponse } from "next/server";
import { AgentPermission, issueLocalAgentJwtFromEnvironment } from "@/lib/local-agent-jwt";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

/**
 * Short-lived Agent token broker.
 *
 * Tokens are least-privilege by default: a token minted with no purpose carries **no** permission
 * claim and can read reader/card status only. `card.read` is granted solely for `purpose:
 * "card-read"`, so the permission exists on the wire only while an operator is actually performing
 * a card read.
 */

/** Purposes a caller may request, and the permissions each one grants. */
const purposePermissions: Record<string, readonly string[]> = {
  status: [],
  "card-read": [AgentPermission.CardRead],
};

const noStoreHeaders = {
  "Cache-Control": "no-store, max-age=0",
  Pragma: "no-cache",
} as const;

export async function POST(request: Request) {
  let purpose = "status";

  // A body is optional; absent or unparseable means the least-privileged token.
  try {
    const body = (await request.json()) as { purpose?: unknown } | null;
    if (body && typeof body.purpose === "string") {
      purpose = body.purpose;
    }
  } catch {
    purpose = "status";
  }

  const permissions = purposePermissions[purpose];
  if (!permissions) {
    return NextResponse.json(
      { error: "UNSUPPORTED_TOKEN_PURPOSE" },
      { status: 400, headers: noStoreHeaders },
    );
  }

  try {
    const issued = issueLocalAgentJwtFromEnvironment(process.env, permissions);
    return NextResponse.json({ ...issued, purpose }, { headers: noStoreHeaders });
  } catch {
    return NextResponse.json(
      { error: "LOCAL_AGENT_TOKEN_UNAVAILABLE" },
      { status: 503, headers: noStoreHeaders },
    );
  }
}
