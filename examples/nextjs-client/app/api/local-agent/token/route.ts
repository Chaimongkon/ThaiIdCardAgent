import { NextResponse } from "next/server";
import { issueLocalAgentJwtFromEnvironment } from "@/lib/local-agent-jwt";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function POST() {
  try {
    const issued = issueLocalAgentJwtFromEnvironment();
    return NextResponse.json(issued, {
      headers: {
        "Cache-Control": "no-store, max-age=0",
        Pragma: "no-cache",
      },
    });
  } catch {
    return NextResponse.json(
      { error: "LOCAL_AGENT_TOKEN_UNAVAILABLE" },
      {
        status: 503,
        headers: {
          "Cache-Control": "no-store, max-age=0",
          Pragma: "no-cache",
        },
      },
    );
  }
}
