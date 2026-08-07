import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ThaiIdAgentPanel } from "@/components/ThaiIdAgentPanel";

/**
 * Main runtime flow of the operator page.
 *
 * The page initializes itself, so these tests assert what happens on mount without any button
 * press, and then drive card state through real SSE frames.
 */

/** Synthetic, checksum-valid. No real citizen ID appears in this repository. */
const syntheticCitizenId = "1101700207366";
const verifyButtonName = "ตรวจสอบการยืนยันตัวตน";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

type ScenarioOptions = {
  readers?: Array<{ name: string; isCardPresent: boolean }>;
  /** Card status per reader name. Falls back to the reader's own flag. */
  cardStatusByReader?: Record<string, "CardPresent" | "NoCard">;
  agentDown?: boolean;
  cardReadResponse?: { body: unknown; status: number };
  memberResponse?: { body: unknown; status: number };
  eventStream?: ReadableStream<Uint8Array>;
  onCardRead?: () => void;
};

function stubAgent(options: ScenarioOptions = {}) {
  const readers = options.readers ?? [{ name: "Reader A", isCardPresent: false }];
  const tokenPurposes: string[] = [];

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (url === "/api/local-agent/token") {
      const body = init?.body ? (JSON.parse(String(init.body)) as { purpose?: string }) : {};
      tokenPurposes.push(body.purpose ?? "(none)");
      return json({ token: "header.payload.signature", expiresAtUtc: "2026-08-07T00:00:30Z", purpose: body.purpose });
    }

    if (url.endsWith("/api/v1/health")) {
      if (options.agentDown) throw new TypeError("Failed to fetch");
      return json({ status: "healthy", service: "ThaiIdCardAgent", version: "1.0.0.0", utcTime: "2026-08-07T00:00:00Z" });
    }

    if (url.endsWith("/api/v1/readers")) {
      return json({
        success: true,
        error: null,
        requestId: "r1",
        data: readers.map((reader) => ({
          name: reader.name,
          isConnected: true,
          isCardPresent: reader.isCardPresent,
          atr: null,
          checkedAtUtc: "2026-08-07T00:00:00Z",
        })),
      });
    }

    if (url.includes("/api/v1/card/status")) {
      const readerName = new URL(url).searchParams.get("readerName") ?? "";
      const fromMap = options.cardStatusByReader?.[readerName];
      const fallback = readers.find((reader) => reader.name === readerName)?.isCardPresent ? "CardPresent" : "NoCard";
      return json({
        success: true,
        error: null,
        requestId: "r2",
        data: { readerName, status: fromMap ?? fallback, atr: null, checkedAtUtc: "2026-08-07T00:00:00Z" },
      });
    }

    if (url.endsWith("/api/v1/events")) {
      if (options.eventStream) {
        return new Response(options.eventStream, { status: 200, headers: { "Content-Type": "text/event-stream" } });
      }
      // Never-ending empty stream so the subscriber stays connected without emitting.
      return new Response(new ReadableStream<Uint8Array>({ start() {} }), {
        status: 200,
        headers: { "Content-Type": "text/event-stream" },
      });
    }

    if (url.endsWith("/api/v1/card/read")) {
      options.onCardRead?.();
      const result = options.cardReadResponse ?? {
        body: { success: false, data: null, error: { code: "THAI_CARD_PROTOCOL_NOT_CONFIGURED", message: "not configured" }, requestId: "r3" },
        status: 501,
      };
      return json(result.body, result.status);
    }

    if (url === "/api/member/verify") {
      const result = options.memberResponse ?? { body: { error: "STAFF_NOT_AUTHENTICATED" }, status: 401 };
      return json(result.body, result.status);
    }

    throw new Error(`Unexpected fetch: ${url}`);
  });

  vi.stubGlobal("fetch", fetchMock);
  return { fetchMock, tokenPurposes };
}

/** Waits until the page finished its automatic initialization. */
async function waitForReady() {
  await waitFor(() => expect(screen.getByText("พร้อมใช้งาน")).toBeInTheDocument());
}

describe("main page automatic initialization", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("checks Agent health on mount with no button press", async () => {
    const { fetchMock } = stubAgent();
    render(<ThaiIdAgentPanel />);

    await waitFor(() => {
      expect(fetchMock.mock.calls.some((call) => String(call[0]).endsWith("/api/v1/health"))).toBe(true);
    });
    await waitForReady();
  });

  it("loads readers automatically and shows the selected reader name", async () => {
    stubAgent({ readers: [{ name: "ACS ACR39U", isCardPresent: false }] });
    render(<ThaiIdAgentPanel />);

    // The name appears in the operator status row and again in the Diagnostics reader picker.
    await waitFor(() => expect(screen.getAllByText("ACS ACR39U").length).toBeGreaterThan(0));
    const statusValue = screen.getByText("ชื่อเครื่องอ่าน").nextElementSibling;
    expect(statusValue).toHaveTextContent("ACS ACR39U");
  });

  it("reports the Agent as unavailable when health fails", async () => {
    stubAgent({ agentDown: true });
    render(<ThaiIdAgentPanel />);

    expect(await screen.findByText("ไม่พร้อมใช้งาน")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled();
  });

  it("reports no reader when the Agent returns an empty list", async () => {
    stubAgent({ readers: [] });
    render(<ThaiIdAgentPanel />);

    expect(await screen.findByText("ไม่พบเครื่องอ่าน")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled();
  });

  it("takes card presence from the selected reader's status endpoint", async () => {
    stubAgent({
      readers: [{ name: "Reader A", isCardPresent: false }],
      cardStatusByReader: { "Reader A": "CardPresent" },
    });
    render(<ThaiIdAgentPanel />);

    expect(await screen.findByText("พบบัตร พร้อมตรวจสอบ")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());
  });

  it("ignores a card in a reader that is not selected", async () => {
    // The old implementation used readers.some(r => r.isCardPresent), which reported a card in the
    // wrong reader on a multi-reader workstation.
    stubAgent({
      readers: [
        { name: "Reader A", isCardPresent: false },
        { name: "Reader B", isCardPresent: true },
      ],
      cardStatusByReader: { "Reader A": "NoCard", "Reader B": "CardPresent" },
    });
    render(<ThaiIdAgentPanel />);

    await waitForReady();
    expect(await screen.findByText("กรุณาเสียบบัตร")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled();
  });

  it("connects SSE automatically and only once", async () => {
    const { fetchMock } = stubAgent();
    render(<ThaiIdAgentPanel />);

    await waitFor(() => {
      expect(fetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/api/v1/events")).length).toBe(1);
    });
    // Give any stray effect re-run a chance to double-subscribe.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(fetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/api/v1/events")).length).toBe(1);
  });

  it("uses a status-purpose token for routine traffic", async () => {
    const { tokenPurposes } = stubAgent();
    render(<ThaiIdAgentPanel />);

    await waitForReady();
    // No card.read on routine status/SSE traffic.
    expect(tokenPurposes.length).toBeGreaterThan(0);
    expect(tokenPurposes).not.toContain("card-read");
  });
});

describe("main page card state via SSE", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function pushableStream() {
    let push: ((chunk: string) => void) | undefined;
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        const encoder = new TextEncoder();
        push = (chunk: string) => controller.enqueue(encoder.encode(chunk));
      },
    });
    return { stream, getPush: () => push };
  }

  function frame(eventType: string, readerName: string) {
    const payload = JSON.stringify({
      eventType,
      readerName,
      cardPresent: eventType === "CardInserted",
      atr: null,
      occurredAtUtc: "2026-08-07T00:00:05Z",
    });
    return `event: ${eventType}\ndata: ${payload}\n\n`;
  }

  it("enables Verify when CardInserted arrives for the selected reader", async () => {
    const { stream, getPush } = pushableStream();
    // Mutable so the status endpoint reflects reality after insertion: CardInserted triggers a
    // re-read of the reader's status, and that read is authoritative.
    const cardStatusByReader: Record<string, "CardPresent" | "NoCard"> = { "Reader A": "NoCard" };
    stubAgent({
      readers: [{ name: "Reader A", isCardPresent: false }],
      cardStatusByReader,
      eventStream: stream,
    });
    render(<ThaiIdAgentPanel />);
    await waitForReady();
    expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled();

    cardStatusByReader["Reader A"] = "CardPresent";
    await waitFor(() => expect(getPush()).toBeDefined());
    getPush()!(frame("CardInserted", "Reader A"));

    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());
    expect(screen.getByText("พบบัตร พร้อมตรวจสอบ")).toBeInTheDocument();
  });

  it("ignores CardInserted for a different reader", async () => {
    const { stream, getPush } = pushableStream();
    stubAgent({
      readers: [
        { name: "Reader A", isCardPresent: false },
        { name: "Reader B", isCardPresent: false },
      ],
      cardStatusByReader: { "Reader A": "NoCard", "Reader B": "NoCard" },
      eventStream: stream,
    });
    render(<ThaiIdAgentPanel />);
    await waitForReady();

    await waitFor(() => expect(getPush()).toBeDefined());
    getPush()!(frame("CardInserted", "Reader B"));

    await new Promise((resolve) => setTimeout(resolve, 60));
    expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled();
    expect(screen.getByText("กรุณาเสียบบัตร")).toBeInTheDocument();
  });

  it("disables Verify when CardRemoved arrives", async () => {
    const { stream, getPush } = pushableStream();
    stubAgent({
      readers: [{ name: "Reader A", isCardPresent: true }],
      cardStatusByReader: { "Reader A": "CardPresent" },
      eventStream: stream,
    });
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await waitFor(() => expect(getPush()).toBeDefined());
    getPush()!(frame("CardRemoved", "Reader A"));

    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled());
    expect(screen.getByText("กรุณาเสียบบัตร")).toBeInTheDocument();
  });

  it("clears a displayed member result when the card is removed", async () => {
    const { stream, getPush } = pushableStream();
    stubAgent({
      readers: [{ name: "Reader A", isCardPresent: true }],
      cardStatusByReader: { "Reader A": "CardPresent" },
      eventStream: stream,
      cardReadResponse: {
        status: 200,
        body: {
          success: true,
          error: null,
          requestId: "r3",
          data: {
            verificationId: "agent-v1",
            readerName: "Reader A",
            citizenId: syntheticCitizenId,
            readAtUtc: "2026-08-07T00:00:01Z",
            providerName: "test",
            cardAtr: null,
          },
        },
      },
      memberResponse: {
        status: 200,
        body: {
          verified: true,
          outcome: "MEMBER_MATCHED",
          verificationId: "v-1",
          memberId: "M-0001",
          memberNo: "00001",
          fullName: "ทดสอบ สมาชิก",
          memberType: "สามัญ",
          memberStatus: "ปกติ",
          photoReference: null,
          maskedCitizenId: "1-1017-xxxxx-36-6",
          verifiedAtUtc: "2026-08-07T00:00:02Z",
        },
      },
    });
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));
    expect(await screen.findByText("M-0001")).toBeInTheDocument();

    await waitFor(() => expect(getPush()).toBeDefined());
    getPush()!(frame("CardRemoved", "Reader A"));

    await waitFor(() => expect(screen.queryByText("M-0001")).not.toBeInTheDocument());
    expect(document.body.textContent).not.toContain(syntheticCitizenId);
    expect(screen.getByRole("button", { name: verifyButtonName })).toBeDisabled();
  });
});

describe("main page verification action", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  const cardPresentScenario: ScenarioOptions = {
    readers: [{ name: "Reader A", isCardPresent: true }],
    cardStatusByReader: { "Reader A": "CardPresent" },
  };

  it("requires an explicit click: no card read happens during initialization", async () => {
    let cardReads = 0;
    stubAgent({ ...cardPresentScenario, onCardRead: () => { cardReads += 1; } });
    render(<ThaiIdAgentPanel />);

    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());
    expect(cardReads).toBe(0);
  });

  it("requests a card-read token only when Verify is clicked", async () => {
    const { tokenPurposes } = stubAgent(cardPresentScenario);
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());
    expect(tokenPurposes).not.toContain("card-read");

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));

    await waitFor(() => expect(tokenPurposes).toContain("card-read"));
  });

  it("shows the provider-not-configured notice on 501", async () => {
    stubAgent(cardPresentScenario);
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));

    expect(await screen.findByText("ยังไม่ได้เชื่อมโมดูลอ่านข้อมูลบัตรประชาชน")).toBeInTheDocument();
  });

  it("does not expose internal exception details", async () => {
    stubAgent({
      ...cardPresentScenario,
      cardReadResponse: {
        status: 500,
        body: {
          success: false,
          data: null,
          error: { code: "INTERNAL_ERROR", message: "boom", technicalDetail: "   at ThaiIdCardAgent.ThaiCard.Secret()" },
          requestId: "r3",
        },
      },
    });
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));

    await waitFor(() => expect(screen.getByRole("alert")).toBeInTheDocument());
    expect(document.body.textContent).not.toContain("at ThaiIdCardAgent");
    expect(document.body.textContent).not.toContain("boom");
  });

  it("prevents double submission", async () => {
    let cardReads = 0;
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const base = stubAgent(cardPresentScenario);
    const original = base.fetchMock.getMockImplementation()!;
    base.fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith("/api/v1/card/read")) {
        cardReads += 1;
        await gate;
      }
      return original(input, init);
    });

    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));
    await waitFor(() => expect(screen.getByRole("button", { name: "กำลังตรวจสอบ..." })).toBeDisabled());

    release?.();
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeInTheDocument());
    expect(cardReads).toBe(1);
  });

  it("never calls the EXAMPLE_ONLY member route", async () => {
    const { fetchMock } = stubAgent({
      ...cardPresentScenario,
      cardReadResponse: {
        status: 200,
        body: {
          success: true,
          error: null,
          requestId: "r3",
          data: {
            verificationId: "agent-v1",
            readerName: "Reader A",
            citizenId: syntheticCitizenId,
            readAtUtc: "2026-08-07T00:00:01Z",
            providerName: "test",
            cardAtr: null,
          },
        },
      },
    });
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));
    await waitFor(() => {
      expect(fetchMock.mock.calls.some((call) => String(call[0]) === "/api/member/verify")).toBe(true);
    });

    for (const call of fetchMock.mock.calls) {
      expect(String(call[0])).not.toContain("/api/member-verification/id-card");
    }
  });

  it("keeps the citizen ID out of storage, the URL, and the console", async () => {
    const spies = [
      vi.spyOn(console, "log").mockImplementation(() => {}),
      vi.spyOn(console, "warn").mockImplementation(() => {}),
      vi.spyOn(console, "error").mockImplementation(() => {}),
    ];
    localStorage.clear();
    sessionStorage.clear();

    const { fetchMock } = stubAgent({
      ...cardPresentScenario,
      cardReadResponse: {
        status: 200,
        body: {
          success: true,
          error: null,
          requestId: "r3",
          data: {
            verificationId: "agent-v1",
            readerName: "Reader A",
            citizenId: syntheticCitizenId,
            readAtUtc: "2026-08-07T00:00:01Z",
            providerName: "test",
            cardAtr: null,
          },
        },
      },
    });
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByRole("button", { name: verifyButtonName })).toBeEnabled());

    await userEvent.click(screen.getByRole("button", { name: verifyButtonName }));
    await waitFor(() => {
      expect(fetchMock.mock.calls.some((call) => String(call[0]) === "/api/member/verify")).toBe(true);
    });

    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    expect(window.location.href).not.toContain(syntheticCitizenId);
    for (const call of fetchMock.mock.calls) {
      expect(String(call[0])).not.toContain(syntheticCitizenId);
    }
    for (const spy of spies) {
      for (const call of spy.mock.calls) {
        expect(JSON.stringify(call)).not.toContain(syntheticCitizenId);
      }
    }
  });
});
