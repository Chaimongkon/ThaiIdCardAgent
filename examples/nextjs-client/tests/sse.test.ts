import { describe, expect, it, vi } from "vitest";
import { parseReaderEvent, parseSseFrames, subscribeReaderEvents } from "@/lib/sse";

function streamResponse(chunks: string[]): Response {
  return new Response(
    new ReadableStream<Uint8Array>({
      start(controller) {
        const encoder = new TextEncoder();
        for (const chunk of chunks) controller.enqueue(encoder.encode(chunk));
        controller.close();
      },
    }),
    { status: 200, headers: { "Content-Type": "text/event-stream" } },
  );
}

describe("SSE parser and client", () => {
  it("parses event, id, and multiline data frames", () => {
    const result = parseSseFrames("id: 1\nevent: CardRemoved\ndata: {\"a\":1}\ndata: {\"b\":2}\n\n");
    expect(result.frames).toEqual([{ id: "1", event: "CardRemoved", data: '{"a":1}\n{"b":2}' }]);
    expect(result.remaining).toBe("");
  });

  it("validates CardRemoved and CardInserted event schema", () => {
    const removed = parseReaderEvent({ event: "CardRemoved", data: JSON.stringify({ eventType: "CardRemoved", readerName: "Reader A", cardPresent: false, occurredAtUtc: "2026-08-04T01:00:00Z" }) });
    const inserted = parseReaderEvent({ event: "CardInserted", data: JSON.stringify({ eventType: "CardInserted", readerName: "Reader A", cardPresent: true, atr: "3B-79", occurredAtUtc: "2026-08-04T01:00:01Z" }) });

    expect(removed.eventType).toBe("CardRemoved");
    expect(removed.atr).toBeNull();
    expect(inserted.eventType).toBe("CardInserted");
    expect(inserted.atr).toBe("3B-79");
  });


  it("accepts the PascalCase ReaderEvent shape emitted by the .NET SSE endpoint", () => {
    const readerEvent = parseReaderEvent({
      event: "CardInserted",
      data: JSON.stringify({ EventType: "CardInserted", ReaderName: "Reader A", CardPresent: true, Atr: "3B-79", OccurredAtUtc: "2026-08-04T01:00:01Z" }),
    });

    expect(readerEvent).toMatchObject({
      eventType: "CardInserted",
      readerName: "Reader A",
      cardPresent: true,
      atr: "3B-79",
      occurredAtUtc: "2026-08-04T01:00:01Z",
    });
  });
  it("rejects non-hex ATR values", () => {
    expect(() => parseReaderEvent({ event: "CardInserted", data: JSON.stringify({ eventType: "CardInserted", readerName: "Reader A", atr: "citizen-name", occurredAtUtc: "2026-08-04T01:00:01Z" }) })).toThrow();
  });

  it("uses a fresh JWT for reconnect attempts", async () => {
    const fetchImpl = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ success: false, data: null, error: { code: "UNAUTHORIZED", message: "Authentication is required." } }), { status: 401 }))
      .mockResolvedValueOnce(streamResponse(["event: CardInserted\ndata: {\"eventType\":\"CardInserted\",\"readerName\":\"Reader A\",\"cardPresent\":true,\"occurredAtUtc\":\"2026-08-04T01:00:01Z\"}\n\n"]));
    let counter = 0;
    const events: string[] = [];

    subscribeReaderEvents({
      baseUrl: "https://localhost:18443",
      fetchImpl,
      getToken: async () => `token-${++counter}`,
      initialReconnectDelayMs: 1,
      maxReconnects: 1,
      onEvent: (event) => events.push(event.eventType),
    });

    await vi.waitFor(() => expect(events).toEqual(["CardInserted"]));
    expect(fetchImpl.mock.calls.map((call) => (call[1]?.headers as Record<string, string>).Authorization)).toEqual(["Bearer token-1", "Bearer token-2"]);
  });

  it("cleans up when the client disconnects", async () => {
    let cancelled = false;
    const fetchImpl = vi.fn(async () => new Response(new ReadableStream<Uint8Array>({ cancel() { cancelled = true; } }), { status: 200 }));
    const unsubscribe = subscribeReaderEvents({ fetchImpl, getToken: async () => "token", onEvent: () => undefined });

    await vi.waitFor(() => expect(fetchImpl).toHaveBeenCalled());
    unsubscribe();
    await vi.waitFor(() => expect(cancelled).toBe(true));
  });
});
