import { AgentClientError, ReaderEvent, ReaderEventType, classifyErrorPayload, normalizeAtr, resolveBaseUrl } from "./thai-id-agent-client";

export type SseConnectionState = "Connecting" | "Connected" | "Reconnecting" | "Disconnected" | "Error";

export type SseFrame = {
  event?: string;
  data: string;
  id?: string;
  retry?: number;
};

export type SseParseResult = {
  frames: SseFrame[];
  remaining: string;
};

export type ReaderEventSubscriptionOptions = {
  baseUrl?: string;
  getToken: () => Promise<string>;
  fetchImpl?: typeof fetch;
  onEvent: (event: ReaderEvent) => void;
  onStateChange?: (state: SseConnectionState) => void;
  onError?: (error: unknown) => void;
  maxReconnects?: number;
  initialReconnectDelayMs?: number;
  maxReconnectDelayMs?: number;
};

const allowedEventTypes = new Set<ReaderEventType>([
  "ReaderConnected",
  "ReaderDisconnected",
  "CardInserted",
  "CardRemoved",
  "StatusChanged",
  "Error",
]);

export function parseSseFrames(input: string, previous = ""): SseParseResult {
  const combined = `${previous}${input}`.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  const chunks = combined.split("\n\n");
  const remaining = chunks.pop() ?? "";
  const frames: SseFrame[] = [];

  for (const chunk of chunks) {
    const lines = chunk.split("\n");
    const data: string[] = [];
    let event: string | undefined;
    let id: string | undefined;
    let retry: number | undefined;

    for (const line of lines) {
      if (!line || line.startsWith(":")) continue;
      const separator = line.indexOf(":");
      const field = separator === -1 ? line : line.slice(0, separator);
      const value = separator === -1 ? "" : line.slice(separator + 1).replace(/^ /, "");
      if (field === "event") event = value;
      if (field === "data") data.push(value);
      if (field === "id") id = value;
      if (field === "retry" && /^\d+$/.test(value)) retry = Number(value);
    }

    if (data.length > 0) frames.push({ event, id, retry, data: data.join("\n") });
  }

  return { frames, remaining };
}

export function parseReaderEvent(frame: SseFrame): ReaderEvent {
  let parsed: unknown;
  try {
    parsed = JSON.parse(frame.data) as unknown;
  } catch {
    throw new AgentClientError("invalid-response", "SSE event data is not valid JSON.");
  }

  if (typeof parsed !== "object" || parsed === null) {
    throw new AgentClientError("invalid-response", "SSE event data is not an object.");
  }

  const candidate = parsed as Partial<ReaderEvent> & {
    EventType?: unknown;
    ReaderName?: unknown;
    CardPresent?: unknown;
    Atr?: unknown;
    OccurredAtUtc?: unknown;
  };
  const eventType = candidate.eventType ?? candidate.EventType;
  const readerName = candidate.readerName ?? candidate.ReaderName;
  const cardPresent = candidate.cardPresent ?? candidate.CardPresent ?? null;
  const atr = candidate.atr ?? candidate.Atr;
  const occurredAtUtc = candidate.occurredAtUtc ?? candidate.OccurredAtUtc;

  if (typeof eventType !== "string" || !allowedEventTypes.has(eventType as ReaderEventType)) {
    throw new AgentClientError("invalid-response", "SSE event type is invalid.");
  }

  if (frame.event && frame.event !== eventType) {
    throw new AgentClientError("invalid-response", "SSE event name does not match event payload.");
  }

  if (!readerName || typeof readerName !== "string") {
    throw new AgentClientError("invalid-response", "SSE readerName is missing.");
  }

  if (typeof occurredAtUtc !== "string" || Number.isNaN(Date.parse(occurredAtUtc))) {
    throw new AgentClientError("invalid-response", "SSE occurredAtUtc is invalid.");
  }

  const safeAtr = typeof atr === "string" ? normalizeAtr(atr) : null;
  if (atr && !safeAtr) {
    throw new AgentClientError("invalid-response", "SSE ATR is not safe hex format.");
  }

  return {
    eventType: eventType as ReaderEventType,
    readerName,
    cardPresent: typeof cardPresent === "boolean" ? cardPresent : null,
    atr: safeAtr,
    occurredAtUtc,
  };
}

export function subscribeReaderEvents(options: ReaderEventSubscriptionOptions): () => void {
  const controller = new AbortController();
  void runEventLoop(options, controller.signal);
  return () => controller.abort();
}

async function runEventLoop(options: ReaderEventSubscriptionOptions, signal: AbortSignal): Promise<void> {
  const maxReconnects = options.maxReconnects ?? 5;
  const initialDelay = options.initialReconnectDelayMs ?? 500;
  const maxDelay = options.maxReconnectDelayMs ?? 5_000;
  let reconnects = 0;

  while (!signal.aborted) {
    options.onStateChange?.(reconnects === 0 ? "Connecting" : "Reconnecting");
    try {
      await readOneEventStream(options, signal);
      if (!signal.aborted) options.onStateChange?.("Disconnected");
      return;
    } catch (error) {
      if (signal.aborted) break;
      options.onError?.(error);
      reconnects += 1;
      if (reconnects > maxReconnects) {
        options.onStateChange?.("Error");
        return;
      }

      const delay = Math.min(maxDelay, initialDelay * 2 ** (reconnects - 1));
      await sleep(delay, signal);
    }
  }

  options.onStateChange?.("Disconnected");
}

async function readOneEventStream(options: ReaderEventSubscriptionOptions, signal: AbortSignal): Promise<void> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const token = await options.getToken();
  const response = await fetchImpl(`${resolveBaseUrl(options.baseUrl)}/api/v1/events`, {
    method: "GET",
    headers: { Accept: "text/event-stream", Authorization: `Bearer ${token}` },
    signal,
    cache: "no-store",
  });

  if (!response.ok) {
    throw classifyErrorPayload(response.status, await readJsonSafely(response));
  }

  if (!response.body) {
    throw new AgentClientError("invalid-response", "SSE response body is missing.");
  }

  options.onStateChange?.("Connected");
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let remaining = "";

  const abortReader = () => { void reader.cancel().catch(() => undefined); };
  signal.addEventListener("abort", abortReader, { once: true });

  try {
    while (!signal.aborted) {
      const { value, done } = await reader.read();
      if (done) return;
      const parsed = parseSseFrames(decoder.decode(value, { stream: true }), remaining);
      remaining = parsed.remaining;
      for (const frame of parsed.frames) options.onEvent(parseReaderEvent(frame));
    }
  } finally {
    signal.removeEventListener("abort", abortReader);
    await reader.cancel().catch(() => undefined);
  }
}

async function readJsonSafely(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
}

function sleep(delayMs: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }

    const timeout = setTimeout(resolve, delayMs);
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timeout);
        resolve();
      },
      { once: true },
    );
  });
}


