import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { issueStaffSessionToken } from "@/lib/staff-identity";
import { SyntheticCitizenIds } from "@/lib/member-directory-mock";

const secret = "test-staff-session-secret";
const originalEnv = { ...process.env };

/** `NODE_ENV` is typed readonly, so tests set it through an indexed write. */
function setNodeEnv(value: string): void {
  (process.env as Record<string, string | undefined>).NODE_ENV = value;
}

function sessionCookie(staffId = "staff-001", workstationId = "counter-01"): string {
  const token = issueStaffSessionToken(
    { staffId, workstationId, department: "สาขาทดสอบ", expiresAtUnixSeconds: Math.floor(Date.now() / 1000) + 600 },
    secret,
  );
  return `coop_staff_session=${encodeURIComponent(token)}`;
}

function post(body: unknown, headers: Record<string, string> = {}): Request {
  return new Request("https://example.invalid/api/member/verify", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}

/** Imported fresh each time so the route observes the current environment. */
async function loadRoute() {
  vi.resetModules();
  return import("@/app/api/member/verify/route");
}

describe("production member verification route", () => {
  beforeEach(() => {
    process.env = { ...originalEnv };
    setNodeEnv("development");
    process.env.STAFF_SESSION_SECRET = secret;
    delete process.env.MEMBER_DB_LOOKUP_SQL;
    delete process.env.MEMBER_VERIFICATION_CORRELATION_KEY;
  });

  afterEach(() => {
    process.env = { ...originalEnv };
    vi.restoreAllMocks();
  });

  // ---- Authentication -------------------------------------------------------------

  it("rejects an unauthenticated request before any lookup", async () => {
    const { POST } = await loadRoute();

    const response = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }));
    const payload = await response.json();

    expect(response.status).toBe(401);
    expect(payload.error).toBe("STAFF_NOT_AUTHENTICATED");
    expect(payload.memberId).toBeUndefined();
  });

  it("ignores a client-supplied staff identity header", async () => {
    const { POST } = await loadRoute();

    const response = await POST(
      post({ citizenId: SyntheticCitizenIds.activeMember }, { "x-staff-id": "attacker" }),
    );

    // No signed session, so a header alone authenticates nobody.
    expect(response.status).toBe(401);
  });

  it("uses the session's staff id and ignores any supplied by the client", async () => {
    const { POST } = await loadRoute();

    const response = await POST(
      post(
        { citizenId: SyntheticCitizenIds.activeMember, staffIdentifier: "attacker-in-body" },
        { cookie: sessionCookie("staff-real"), "x-staff-id": "attacker-in-header" },
      ),
    );
    const payload = await response.json();

    // The signed session authenticates the request; the lookup then reports the database as
    // unconfigured. What matters here is that the client-chosen identity was never adopted.
    expect(response.status).toBe(503);
    expect(payload.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
    expect(JSON.stringify(payload)).not.toContain("attacker-in-body");
    expect(JSON.stringify(payload)).not.toContain("attacker-in-header");
  });

  it("rejects a session signed with the wrong secret", async () => {
    const { POST } = await loadRoute();
    const foreign = issueStaffSessionToken(
      { staffId: "staff-001", workstationId: "counter-01", expiresAtUnixSeconds: Math.floor(Date.now() / 1000) + 600 },
      "attacker-secret",
    );

    const response = await POST(
      post({ citizenId: SyntheticCitizenIds.activeMember }, { cookie: `coop_staff_session=${encodeURIComponent(foreign)}` }),
    );

    expect(response.status).toBe(401);
    expect((await response.json()).reason).toBe("SESSION_SIGNATURE_INVALID");
  });

  it("rejects every request when no staff session secret is configured", async () => {
    delete process.env.STAFF_SESSION_SECRET;
    const { POST } = await loadRoute();

    const response = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }, { cookie: sessionCookie() }));

    expect(response.status).toBe(401);
    expect((await response.json()).reason).toBe("NOT_CONFIGURED");
  });

  // ---- Directory resolution --------------------------------------------------------

  it("reports MEMBER_DB_NOT_CONFIGURED when no member database is configured", async () => {
    delete process.env.MEMBER_DB_LOOKUP_SQL;
    const { POST } = await loadRoute();

    const response = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }, { cookie: sessionCookie() }));
    const payload = await response.json();

    expect(response.status).toBe(503);
    expect(payload.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
    // An unconfigured directory must never look like a genuine miss.
    expect(payload.outcome).not.toBe("MEMBER_NOT_FOUND");
  });

  it("never falls back to MockMemberDirectory, even in development", async () => {
    // The mock returns fabricated members. There is deliberately no environment switch that can
    // route this route to it — mock data is reachable only through /dev/member-verification.
    setNodeEnv("development");
    process.env.MEMBER_DIRECTORY_USE_MOCK = "true";
    delete process.env.MEMBER_DB_LOOKUP_SQL;
    const { POST } = await loadRoute();

    // This ID matches a mock member; the route must still report the database as unconfigured.
    const response = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }, { cookie: sessionCookie() }));
    const payload = await response.json();

    expect(response.status).toBe(503);
    expect(payload.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
    expect(payload.memberId).toBeNull();
    expect(JSON.stringify(payload)).not.toContain("MOCK-M-");
  });

  it("never falls back to MockMemberDirectory in production either", async () => {
    setNodeEnv("production");
    process.env.MEMBER_DIRECTORY_USE_MOCK = "true";
    delete process.env.MEMBER_DB_LOOKUP_SQL;
    const { POST } = await loadRoute();

    const response = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }, { cookie: sessionCookie() }));
    const payload = await response.json();

    expect(response.status).toBe(503);
    expect(payload.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
    expect(JSON.stringify(payload)).not.toContain("MOCK-M-");
  });

  it("rejects a malformed citizen ID with 422 and does not echo it", async () => {
    const { POST } = await loadRoute();
    const malformed = "110170020736X";

    const response = await POST(post({ citizenId: malformed }, { cookie: sessionCookie() }));
    const payload = await response.json();

    // Validation happens before any lookup, so this is reported even with no database configured.
    expect(response.status).toBe(422);
    expect(payload.outcome).toBe("CITIZEN_ID_INVALID");
    expect(JSON.stringify(payload)).not.toContain(malformed);
  });

  // ---- Response hygiene -----------------------------------------------------------

  it("sets no-store on every response", async () => {
    const { POST } = await loadRoute();

    const ok = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }, { cookie: sessionCookie() }));
    const rejected = await POST(post({ citizenId: SyntheticCitizenIds.activeMember }));

    expect(ok.headers.get("Cache-Control")).toContain("no-store");
    expect(rejected.headers.get("Cache-Control")).toContain("no-store");
  });

  it("rejects a malformed request body with 400", async () => {
    const { POST } = await loadRoute();
    const request = new Request("https://example.invalid/api/member/verify", {
      method: "POST",
      headers: { "Content-Type": "application/json", cookie: sessionCookie() },
      body: "not json",
    });

    const response = await POST(request);

    expect(response.status).toBe(400);
    expect((await response.json()).error).toBe("INVALID_REQUEST");
  });
});

describe("example route classification", () => {
  beforeEach(() => {
    process.env = { ...originalEnv };
    setNodeEnv("development");
  });

  afterEach(() => {
    process.env = { ...originalEnv };
  });

  it("is marked EXAMPLE_ONLY_NOT_FOR_PRODUCTION and says so on the wire", async () => {
    vi.resetModules();
    const { POST, EXAMPLE_ROUTE_CLASSIFICATION } = await import("@/app/api/member-verification/id-card/route");

    expect(EXAMPLE_ROUTE_CLASSIFICATION).toBe("EXAMPLE_ONLY_NOT_FOR_PRODUCTION");

    const response = await POST(
      new Request("https://example.invalid/api/member-verification/id-card", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ citizenId: SyntheticCitizenIds.activeMember }),
      }),
    );

    expect(response.headers.get("X-Route-Classification")).toBe("EXAMPLE_ONLY_NOT_FOR_PRODUCTION");
  });

  it("refuses to run outside development", async () => {
    setNodeEnv("production");
    vi.resetModules();
    const { POST } = await import("@/app/api/member-verification/id-card/route");

    const response = await POST(
      new Request("https://example.invalid/api/member-verification/id-card", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ citizenId: SyntheticCitizenIds.activeMember }),
      }),
    );

    expect(response.status).toBe(404);
    expect((await response.json()).error).toBe("EXAMPLE_ONLY_NOT_FOR_PRODUCTION");
  });
});
