/**
 * ============================================================================================
 * DEVELOPMENT ONLY — EXAMPLE_ONLY_NOT_FOR_PRODUCTION
 * ============================================================================================
 *
 * Manual member verification harness at `/dev/member-verification`.
 *
 * The official Thai card provider is blocked, so no physical card can be read. This page lets a
 * developer pick a synthetic scenario, run it through the real verification route, and see the
 * resulting Member Card — proving the flow works while the card provider is unavailable.
 *
 * The environment gate lives here, in a **server component**, so it cannot be bypassed from the
 * browser: outside development the route resolves to `notFound()` and the client bundle for the
 * panel is never sent. `force-dynamic` keeps the check per-request rather than baking a build-time
 * decision into a static page.
 */

import { notFound } from "next/navigation";
import { DevMemberVerificationPanel } from "@/components/DevMemberVerificationPanel";

export const dynamic = "force-dynamic";

export const metadata = {
  title: "Dev — Member Verification (mock data)",
  robots: { index: false, follow: false },
};

/** Exported so tests assert the gate rather than re-deriving the condition. */
export function isDevMemberVerificationEnabled(
  env: Record<string, string | undefined> = process.env,
): boolean {
  return (env.NODE_ENV ?? "development") === "development";
}

export default function DevMemberVerificationPage() {
  if (!isDevMemberVerificationEnabled()) {
    notFound();
  }

  return <DevMemberVerificationPanel />;
}
