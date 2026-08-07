import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DEV_BANNER_TEXT, DevMemberVerificationPanel } from "@/components/DevMemberVerificationPanel";
import { SyntheticCitizenIds } from "@/lib/member-directory-mock";

const originalEnv = { ...process.env };

/** `NODE_ENV` is typed readonly, so tests set it through an indexed write. */
function setNodeEnv(value: string): void {
  (process.env as Record<string, string | undefined>).NODE_ENV = value;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const matchedResponse = {
  verified: true,
  outcome: "MEMBER_MATCHED",
  verificationId: "dev-v-1",
  memberId: "MOCK-M-0001",
  memberNo: "000001",
  fullName: "ทดสอบ สมาชิกสามัญ",
  memberType: "สามัญ",
  memberStatus: "ปกติ",
  photoReference: null,
  maskedCitizenId: "1-0000-xxxxx-00-9",
  verifiedAtUtc: "2026-08-06T00:00:00Z",
};

const resignedResponse = {
  ...matchedResponse,
  verificationId: "dev-v-4",
  memberId: "MOCK-M-0003",
  memberNo: "000003",
  fullName: "ทดสอบ สมาชิกลาออก",
  memberStatus: "ลาออก",
  maskedCitizenId: "5-9990-xxxxx-22-9",
};

const notFoundResponse = {
  verified: false,
  outcome: "MEMBER_NOT_FOUND",
  verificationId: "dev-v-2",
  memberId: null,
  memberNo: null,
  fullName: null,
  memberType: null,
  memberStatus: null,
  photoReference: null,
  maskedCitizenId: "1-1017-xxxxx-36-6",
  verifiedAtUtc: "2026-08-06T00:00:00Z",
};

const duplicateResponse = {
  ...notFoundResponse,
  verificationId: "dev-v-3",
  outcome: "MEMBER_DUPLICATE",
  maskedCitizenId: "3-1006-xxxxx-71-6",
};

/** Records every citizen ID posted, so tests can assert which scenario was sent. */
function stubVerification(responder: (citizenId: string) => { body: unknown; status?: number }) {
  const sent: string[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    void input;
    const body = JSON.parse(String(init?.body ?? "{}")) as { citizenId?: string };
    if (body.citizenId) sent.push(body.citizenId);
    const result = responder(body.citizenId ?? "");
    return json(result.body, result.status ?? 200);
  });
  vi.stubGlobal("fetch", fetchMock);
  return { fetchMock, sent };
}

const runButtonName = "จำลองการอ่านบัตรและตรวจสอบสมาชิก";
const cardRemovedButtonName = "จำลองการถอดบัตร (CardRemoved)";

describe("dev member verification page gate", () => {
  beforeEach(() => {
    process.env = { ...originalEnv };
  });

  afterEach(() => {
    process.env = { ...originalEnv };
    vi.resetModules();
  });

  it("is enabled in development", async () => {
    setNodeEnv("development");
    vi.resetModules();
    const { isDevMemberVerificationEnabled } = await import("@/app/dev/member-verification/page");

    expect(isDevMemberVerificationEnabled()).toBe(true);
    expect(isDevMemberVerificationEnabled({ NODE_ENV: "development" })).toBe(true);
  });

  it("is disabled in production and in test/staging builds", async () => {
    vi.resetModules();
    const { isDevMemberVerificationEnabled } = await import("@/app/dev/member-verification/page");

    expect(isDevMemberVerificationEnabled({ NODE_ENV: "production" })).toBe(false);
    expect(isDevMemberVerificationEnabled({ NODE_ENV: "staging" })).toBe(false);
    expect(isDevMemberVerificationEnabled({ NODE_ENV: "test" })).toBe(false);
  });

  it("renders the panel in development", async () => {
    setNodeEnv("development");
    vi.resetModules();
    const { default: Page } = await import("@/app/dev/member-verification/page");

    const element = Page();

    expect(element).not.toBeNull();
  });

  it("calls notFound() in production so the route resolves to 404", async () => {
    setNodeEnv("production");
    vi.resetModules();
    const { default: Page } = await import("@/app/dev/member-verification/page");

    // next/navigation's notFound() signals the 404 by throwing; the framework catches it and
    // renders the not-found page. The digest spelling differs across Next versions
    // ("NEXT_NOT_FOUND" historically, "NEXT_HTTP_ERROR_FALLBACK;404" in Next 16), so both are
    // accepted — what matters is that a 404 signal is raised.
    expect(() => Page()).toThrow();
    try {
      Page();
      throw new Error("Page() did not throw in production.");
    } catch (error) {
      const digest = (error as { digest?: string }).digest ?? String(error);
      expect(digest).toMatch(/NEXT_NOT_FOUND|NEXT_HTTP_ERROR_FALLBACK;404/);
    }
  });

  it("is excluded from search indexing", async () => {
    vi.resetModules();
    const { metadata } = await import("@/app/dev/member-verification/page");

    expect(metadata.robots).toEqual({ index: false, follow: false });
  });
});

describe("dev member verification panel", () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows the mock-data banner", () => {
    stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    expect(DEV_BANNER_TEXT).toBe("ข้อมูลจำลองสำหรับการพัฒนา — ไม่ได้อ่านจากบัตรจริง");
    const banner = screen.getByLabelText("Development mock data banner");
    expect(banner).toHaveTextContent(DEV_BANNER_TEXT);
    // The banner is an alert so assistive technology announces it, not just sighted users.
    expect(banner).toHaveAttribute("role", "alert");
  });

  it("offers exactly the four required scenarios", () => {
    stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    const options = screen.getAllByRole("option").map((option) => option.textContent);
    expect(options).toHaveLength(4);
    expect(options.join("|")).toContain("พบข้อมูลสมาชิก");
    expect(options.join("|")).toContain("ไม่พบข้อมูลสมาชิก");
    expect(options.join("|")).toContain("ซ้ำซ้อน");
    expect(options.join("|")).toContain("ลาออก");
  });

  it("posts the matched-member citizen ID and renders the Member Card", async () => {
    const { sent } = stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));

    expect(await screen.findByText("MOCK-M-0001")).toBeInTheDocument();
    expect(screen.getByText("000001")).toBeInTheDocument();
    expect(screen.getByText("ทดสอบ สมาชิกสามัญ")).toBeInTheDocument();
    expect(screen.getByText("สามัญ")).toBeInTheDocument();
    expect(screen.getByText("ปกติ")).toBeInTheDocument();
    expect(sent).toEqual([SyntheticCitizenIds.activeMember]);
  });

  it("sends each selected scenario's citizen ID", async () => {
    const { sent } = stubVerification((citizenId) => {
      if (citizenId === SyntheticCitizenIds.unknownMember) return { body: notFoundResponse };
      if (citizenId === SyntheticCitizenIds.duplicatedMember) return { body: duplicateResponse, status: 409 };
      if (citizenId === SyntheticCitizenIds.inactiveMember) return { body: resignedResponse };
      return { body: matchedResponse };
    });
    render(<DevMemberVerificationPanel />);
    const select = screen.getByRole("combobox");

    await userEvent.selectOptions(select, "notFound");
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findAllByText(/ไม่พบข้อมูลสมาชิก/);

    await userEvent.selectOptions(select, "duplicate");
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findAllByText(/ซ้ำซ้อน/);

    await userEvent.selectOptions(select, "resigned");
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findByText("MOCK-M-0003");

    expect(sent).toEqual([
      SyntheticCitizenIds.unknownMember,
      SyntheticCitizenIds.duplicatedMember,
      SyntheticCitizenIds.inactiveMember,
    ]);
  });

  it("shows only the masked citizen ID, never the raw value", async () => {
    stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findByText("MOCK-M-0001");

    expect(screen.getByText("1-0000-xxxxx-00-9")).toBeInTheDocument();
    expect(document.body.textContent).not.toContain(SyntheticCitizenIds.activeMember);
  });

  it("separates identity matching from transaction eligibility", async () => {
    // Reading "identity matched" as "cleared to transact" is exactly the wrong inference.
    stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findByText("MOCK-M-0001");

    expect(screen.getByText("การยืนยันตัวตน")).toBeInTheDocument();
    expect(screen.getByText("สิทธิ์ในการทำธุรกรรม")).toBeInTheDocument();
    expect(screen.getByText(/ระบบนี้ไม่ได้ตัดสินสิทธิ์/)).toBeInTheDocument();
  });

  it("flags a resigned member as matched-but-restricted", async () => {
    stubVerification(() => ({ body: resignedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.selectOptions(screen.getByRole("combobox"), "resigned");
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));

    expect(await screen.findByText("MOCK-M-0003")).toBeInTheDocument();
    expect(screen.getByText("ลาออก")).toBeInTheDocument();
    expect(screen.getByText(/สถานะสมาชิกไม่ปกติ/)).toBeInTheDocument();
  });

  it("discloses no member data for a duplicate", async () => {
    stubVerification(() => ({ body: duplicateResponse, status: 409 }));
    render(<DevMemberVerificationPanel />);

    await userEvent.selectOptions(screen.getByRole("combobox"), "duplicate");
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));

    expect(await screen.findAllByText(/ซ้ำซ้อน/)).not.toHaveLength(0);
    expect(screen.queryByText("MOCK-M-0005")).not.toBeInTheDocument();
    expect(screen.queryByText("MOCK-M-0006")).not.toBeInTheDocument();
    expect(screen.getByText(/ไม่แสดงข้อมูลสมาชิกเมื่อยืนยันตัวตนไม่สำเร็จ/)).toBeInTheDocument();
  });

  it("shows no member data for not-found", async () => {
    stubVerification(() => ({ body: notFoundResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.selectOptions(screen.getByRole("combobox"), "notFound");
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));

    await screen.findAllByText(/ไม่พบข้อมูลสมาชิก/);
    expect(screen.queryByText("MOCK-M-0001")).not.toBeInTheDocument();
  });

  it("clears the Member Card when CardRemoved is simulated", async () => {
    stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    expect(await screen.findByText("MOCK-M-0001")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: cardRemovedButtonName }));

    await waitFor(() => expect(screen.queryByText("MOCK-M-0001")).not.toBeInTheDocument());
    expect(screen.queryByText("ทดสอบ สมาชิกสามัญ")).not.toBeInTheDocument();
    expect(screen.queryByText("1-0000-xxxxx-00-9")).not.toBeInTheDocument();
    // "CardRemoved" also appears in the button label, so scope the check to the status notice.
    const alerts = screen.getAllByRole("alert");
    expect(alerts.some((alert) => alert.textContent?.includes("CardRemoved"))).toBe(true);
  });

  it("prevents double submission while a request is in flight", async () => {
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    let calls = 0;
    const fetchMock = vi.fn(async () => {
      calls += 1;
      await gate;
      return json(matchedResponse);
    });
    vi.stubGlobal("fetch", fetchMock);

    render(<DevMemberVerificationPanel />);
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await waitFor(() => expect(screen.getByRole("button", { name: "กำลังตรวจสอบ..." })).toBeDisabled());

    release?.();
    await screen.findByText("MOCK-M-0001");
    expect(calls).toBe(1);
  });

  it("keeps the citizen ID out of the URL, storage, and the console", async () => {
    const spies = [
      vi.spyOn(console, "log").mockImplementation(() => {}),
      vi.spyOn(console, "info").mockImplementation(() => {}),
      vi.spyOn(console, "warn").mockImplementation(() => {}),
      vi.spyOn(console, "error").mockImplementation(() => {}),
      vi.spyOn(console, "debug").mockImplementation(() => {}),
    ];
    const { fetchMock } = stubVerification(() => ({ body: matchedResponse }));

    render(<DevMemberVerificationPanel />);
    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findByText("MOCK-M-0001");

    const raw = SyntheticCitizenIds.activeMember;
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    expect(JSON.stringify(localStorage)).not.toContain(raw);
    expect(JSON.stringify(sessionStorage)).not.toContain(raw);
    expect(window.location.href).not.toContain(raw);
    expect(window.location.search).not.toContain(raw);
    expect(document.body.textContent).not.toContain(raw);

    // The identifier travels in the request body only, never in a URL.
    for (const call of fetchMock.mock.calls) {
      expect(String(call[0])).not.toContain(raw);
    }
    for (const spy of spies) {
      for (const call of spy.mock.calls) {
        expect(JSON.stringify(call)).not.toContain(raw);
      }
    }
  });

  it("posts to the example verification route with no-store", async () => {
    const { fetchMock } = stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findByText("MOCK-M-0001");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/member-verification/id-card");
    expect(init.method).toBe("POST");
    expect(init.cache).toBe("no-store");
  });

  it("never contacts the local agent", async () => {
    // This harness substitutes for the card read; it must not reach the agent at all.
    const { fetchMock } = stubVerification(() => ({ body: matchedResponse }));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));
    await screen.findByText("MOCK-M-0001");

    for (const call of fetchMock.mock.calls) {
      expect(String(call[0])).not.toContain("/api/v1/");
      expect(String(call[0])).not.toContain("18443");
    }
  });

  it("reports a route-level failure without crashing", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => json({ error: "EXAMPLE_ONLY_NOT_FOR_PRODUCTION" }, 404)));
    render(<DevMemberVerificationPanel />);

    await userEvent.click(screen.getByRole("button", { name: runButtonName }));

    expect(await screen.findByText(/ตรวจสอบไม่สำเร็จ/)).toBeInTheDocument();
    // The classification string also appears in the banner, so assert on the status notice.
    const notices = screen.getAllByRole("alert");
    expect(notices.some((notice) => notice.textContent?.includes("EXAMPLE_ONLY_NOT_FOR_PRODUCTION"))).toBe(true);
  });
});
