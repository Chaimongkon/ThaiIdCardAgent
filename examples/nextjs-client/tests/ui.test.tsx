import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ThaiIdAgentPanel } from "@/components/ThaiIdAgentPanel";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function stubAgent(cardPresent: boolean) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === "/api/local-agent/token") return json({ token: "a.b.c", expiresAtUtc: "2026-08-07T00:00:30Z" });
    if (url.endsWith("/api/v1/health")) {
      return json({ status: "healthy", service: "ThaiIdCardAgent", version: "1.0.0.0", utcTime: "2026-08-07T00:00:00Z" });
    }
    if (url.endsWith("/api/v1/readers")) {
      return json({
        success: true,
        error: null,
        requestId: "r1",
        data: [{ name: "Reader A", isConnected: true, isCardPresent: cardPresent, atr: "3B-79", checkedAtUtc: "2026-08-07T00:00:00Z" }],
      });
    }
    if (url.includes("/api/v1/card/status")) {
      return json({
        success: true,
        error: null,
        requestId: "r2",
        data: { readerName: "Reader A", status: cardPresent ? "CardPresent" : "NoCard", atr: "3B-79", checkedAtUtc: "2026-08-07T00:00:00Z" },
      });
    }
    if (url.endsWith("/api/v1/events")) {
      return new Response(new ReadableStream<Uint8Array>({ start() {} }), {
        status: 200,
        headers: { "Content-Type": "text/event-stream" },
      });
    }
    throw new Error(`Unexpected fetch: ${url}`);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("ThaiIdAgentPanel operator view", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("presents the four operator status rows and the single primary action", async () => {
    stubAgent(false);
    render(<ThaiIdAgentPanel />);

    await waitFor(() => expect(screen.getByText("พร้อมใช้งาน")).toBeInTheDocument());
    expect(screen.getByText("Agent")).toBeInTheDocument();
    expect(screen.getByText("เครื่องอ่านบัตร")).toBeInTheDocument();
    expect(screen.getByText("ชื่อเครื่องอ่าน")).toBeInTheDocument();
    expect(screen.getByText("บัตรประชาชน")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "ตรวจสอบการยืนยันตัวตน" })).toBeInTheDocument();
  });

  it("keeps diagnostics available but out of the primary flow", async () => {
    stubAgent(false);
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByText("พร้อมใช้งาน")).toBeInTheDocument());

    // Manual controls still exist for troubleshooting, inside a collapsed Diagnostics section.
    const diagnostics = screen.getByText("Diagnostics / ขั้นสูง").closest("details");
    expect(diagnostics).not.toBeNull();
    expect(diagnostics!.open).toBe(false);
    expect(screen.getByRole("button", { name: "Check Agent" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Refresh Readers" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Read ATR" })).toBeInTheDocument();
  });

  it("no longer renders the dead permanently-disabled Card Read button", async () => {
    stubAgent(false);
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByText("พร้อมใช้งาน")).toBeInTheDocument());

    expect(screen.queryByRole("button", { name: "Card Read" })).not.toBeInTheDocument();
  });

  it("states that only the citizen ID is read", async () => {
    stubAgent(false);
    render(<ThaiIdAgentPanel />);
    await waitFor(() => expect(screen.getByText("พร้อมใช้งาน")).toBeInTheDocument());

    expect(screen.getByText(/ไม่อ่านรูปถ่าย ที่อยู่ ชื่อ หรือวันเกิดจากบัตร/)).toBeInTheDocument();
  });
});
