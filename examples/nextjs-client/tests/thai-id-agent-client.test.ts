import { describe, expect, it, vi } from "vitest";
import { AgentClientError, getCardStatus, getReaders, readCardAtr } from "@/lib/thai-id-agent-client";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("ThaiIdAgent typed client", () => {
  it("uses a fresh JWT for every protected request", async () => {
    const tokens: string[] = [];
    const fetchImpl = vi.fn(async (_url: string | URL | Request, init?: RequestInit) => {
      tokens.push(String((init?.headers as Record<string, string>).Authorization));
      return jsonResponse({ success: true, data: [], error: null, requestId: "r1" });
    });
    let counter = 0;
    const getToken = vi.fn(async () => `token-${++counter}`);

    await getReaders({ baseUrl: "https://localhost:18443", fetchImpl, getToken });
    await getReaders({ baseUrl: "https://localhost:18443", fetchImpl, getToken });

    expect(getToken).toHaveBeenCalledTimes(2);
    expect(tokens).toEqual(["Bearer token-1", "Bearer token-2"]);
  });

  it("maps card-not-present errors without exposing stack traces", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse({ success: false, data: null, error: { code: "CARD_NOT_PRESENT", message: "No card." }, requestId: "req" }, 422));

    await expect(readCardAtr(undefined, { fetchImpl, getToken: async () => "token" })).rejects.toMatchObject({
      kind: "card-not-present",
      code: "CARD_NOT_PRESENT",
    });
  });

  it("maps replay/auth errors separately from card state", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse({ success: false, data: null, error: { code: "UNAUTHORIZED", message: "Authentication is required." }, requestId: "req" }, 401));

    await expect(getCardStatus(undefined, { fetchImpl, getToken: async () => "token" })).rejects.toMatchObject({
      kind: "auth",
      code: "UNAUTHORIZED",
    });
  });

  it("classifies fetch failures as TLS or network failures", async () => {
    const fetchImpl = vi.fn(async () => {
      throw new TypeError("fetch failed");
    });

    await expect(getReaders({ fetchImpl, getToken: async () => "token" })).rejects.toBeInstanceOf(AgentClientError);
    await expect(getReaders({ fetchImpl, getToken: async () => "token" })).rejects.toMatchObject({ kind: "tls-or-network" });
  });
});
