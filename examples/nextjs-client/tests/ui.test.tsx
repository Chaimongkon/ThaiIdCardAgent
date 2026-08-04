import React from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ThaiIdAgentPanel } from "@/components/ThaiIdAgentPanel";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("ThaiIdAgentPanel", () => {
  it("renders required pilot controls and keeps card read disabled", () => {
    render(<ThaiIdAgentPanel />);

    expect(screen.getByRole("button", { name: "Check Agent" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Refresh Readers" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Connect Events" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Card Read" })).toBeDisabled();
    expect(screen.getByText(/Thai card protocol endpoint remains disabled/)).toBeInTheDocument();
  });

  it("shows reader, CardPresent state, and uppercase ATR from mocked Agent APIs", async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/local-agent/token") return json({ token: "header.payload.signature", expiresAtUtc: "2026-08-04T00:00:30Z" });
      if (url.endsWith("/api/v1/readers")) return json({ success: true, data: [{ name: "Reader A", isConnected: true, isCardPresent: true, atr: "3B-79", checkedAtUtc: "2026-08-04T00:00:00Z" }], error: null, requestId: "r1" });
      if (url.endsWith("/api/v1/card/atr")) return json({ success: true, data: { readerName: "Reader A", atr: "3B-79", readAtUtc: "2026-08-04T00:00:01Z" }, error: null, requestId: "r2" });
      return json({ status: "healthy", service: "ThaiIdCardAgent", version: "1.0.0.0", utcTime: "2026-08-04T00:00:00Z" });
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<ThaiIdAgentPanel />);

    await userEvent.click(screen.getByRole("button", { name: "Refresh Readers" }));
    expect(await screen.findByText("Reader A")).toBeInTheDocument();
    expect(screen.getByText("CardPresent")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Read ATR" }));
    expect(await screen.findAllByText("3B-79")).not.toHaveLength(0);
    expect(fetchMock.mock.calls.filter((call) => String(call[0]) === "/api/local-agent/token")).toHaveLength(2);
  });
});

