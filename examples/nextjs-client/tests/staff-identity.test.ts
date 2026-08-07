import { describe, expect, it } from "vitest";
import {
  SignedSessionStaffIdentityProvider,
  UnconfiguredStaffIdentityProvider,
  issueStaffSessionToken,
} from "@/lib/staff-identity";

const secret = "test-staff-session-secret";
const cookieName = "coop_staff_session";

function requestWithCookie(token: string | null, extraHeaders: Record<string, string> = {}): Request {
  const headers: Record<string, string> = { ...extraHeaders };
  if (token !== null) headers.cookie = `${cookieName}=${encodeURIComponent(token)}`;
  return new Request("https://example.invalid/api/member/verify", { method: "POST", headers });
}

function validToken(overrides: Partial<Parameters<typeof issueStaffSessionToken>[0]> = {}) {
  return issueStaffSessionToken(
    {
      staffId: "staff-001",
      displayName: "เจ้าหน้าที่ ทดสอบ",
      department: "สาขาสำนักงานใหญ่",
      workstationId: "counter-01",
      expiresAtUnixSeconds: Math.floor(Date.now() / 1000) + 600,
      ...overrides,
    },
    secret,
  );
}

describe("UnconfiguredStaffIdentityProvider", () => {
  it("authenticates nobody", async () => {
    // A deployment that has not wired staff auth must reject, never fall back to a placeholder.
    const result = await new UnconfiguredStaffIdentityProvider().resolve();
    expect(result).toEqual({ authenticated: false, reason: "NOT_CONFIGURED" });
  });
});

describe("SignedSessionStaffIdentityProvider", () => {
  const provider = new SignedSessionStaffIdentityProvider({ secret, cookieName });

  it("accepts a session the server itself signed", async () => {
    const result = await provider.resolve(requestWithCookie(validToken()));

    expect(result.authenticated).toBe(true);
    if (!result.authenticated) return;
    expect(result.identity).toEqual({
      staffId: "staff-001",
      displayName: "เจ้าหน้าที่ ทดสอบ",
      department: "สาขาสำนักงานใหญ่",
      workstationId: "counter-01",
    });
  });

  it("rejects a request with no session", async () => {
    expect(await provider.resolve(requestWithCookie(null))).toEqual({ authenticated: false, reason: "NO_SESSION" });
  });

  it("rejects a forged payload whose signature does not match", async () => {
    // This is the attack the signature exists to stop: rewriting the operator id.
    const token = validToken();
    const [payload, signature] = token.split(".");
    const forgedPayload = Buffer.from(
      JSON.stringify({ staffId: "someone-else", workstationId: "counter-01", exp: Math.floor(Date.now() / 1000) + 600 }),
      "utf8",
    ).toString("base64url");

    const result = await provider.resolve(requestWithCookie(`${forgedPayload}.${signature}`));

    expect(result).toEqual({ authenticated: false, reason: "SESSION_SIGNATURE_INVALID" });
    void payload;
  });

  it("rejects a token signed with a different secret", async () => {
    const foreign = issueStaffSessionToken(
      { staffId: "staff-001", workstationId: "counter-01", expiresAtUnixSeconds: Math.floor(Date.now() / 1000) + 600 },
      "attacker-secret",
    );

    expect(await provider.resolve(requestWithCookie(foreign))).toEqual({
      authenticated: false,
      reason: "SESSION_SIGNATURE_INVALID",
    });
  });

  it("rejects an expired session", async () => {
    const expired = validToken({ expiresAtUnixSeconds: Math.floor(Date.now() / 1000) - 1 });

    expect(await provider.resolve(requestWithCookie(expired))).toEqual({
      authenticated: false,
      reason: "SESSION_EXPIRED",
    });
  });

  it("rejects a session missing the staff id or workstation", async () => {
    const incomplete = issueStaffSessionToken(
      { staffId: "", workstationId: "counter-01", expiresAtUnixSeconds: Math.floor(Date.now() / 1000) + 600 },
      secret,
    );

    expect(await provider.resolve(requestWithCookie(incomplete))).toEqual({
      authenticated: false,
      reason: "SESSION_INCOMPLETE",
    });
  });

  it("rejects a malformed token", async () => {
    expect((await provider.resolve(requestWithCookie("not-a-token"))).authenticated).toBe(false);
    expect((await provider.resolve(requestWithCookie("only.")))!.authenticated).toBe(false);
  });

  it("ignores client-supplied identity headers entirely", async () => {
    // Headers are chosen by the client; only the signed session decides who the operator is.
    const result = await provider.resolve(
      requestWithCookie(validToken(), {
        "x-staff-id": "attacker",
        "x-user-id": "attacker",
        "x-operator-id": "attacker",
        "x-workstation-id": "attacker-workstation",
      }),
    );

    expect(result.authenticated).toBe(true);
    if (!result.authenticated) return;
    expect(result.identity.staffId).toBe("staff-001");
    expect(result.identity.workstationId).toBe("counter-01");
  });

  it("does not authenticate from a header when no session cookie is present", async () => {
    const result = await provider.resolve(requestWithCookie(null, { "x-staff-id": "attacker" }));

    expect(result).toEqual({ authenticated: false, reason: "NO_SESSION" });
  });

  it("reports not-configured when no secret is set", async () => {
    const unconfigured = new SignedSessionStaffIdentityProvider({ secret: "" });

    expect(await unconfigured.resolve(requestWithCookie(validToken()))).toEqual({
      authenticated: false,
      reason: "NOT_CONFIGURED",
    });
  });
});
