export type AgentCredential = string | { type: "developmentKey" | "bearerToken"; value: string };

export type AgentHealth = {
  status: string;
  service: string;
  version: string;
  utcTime: string;
};

export type AgentError = {
  code: string;
  message: string;
  technicalDetail?: string | null;
};

export type OperationResult<T> = {
  success: boolean;
  data: T | null;
  error: AgentError | null;
  requestId?: string | null;
};

export type SmartCardReaderInfo = {
  name: string;
  isConnected: boolean;
  isCardPresent: boolean;
  atr?: string | null;
  checkedAtUtc: string;
};

export type SmartCardStatus = {
  readerName: string;
  status: "Unknown" | "ReaderUnavailable" | "NoCard" | "CardPresent" | "CardMute" | "CardUnpowered";
  atr?: string | null;
  checkedAtUtc: string;
};

export type CardAtrResponse = {
  readerName: string;
  atr: string;
  readAtUtc: string;
};

export type ThaiIdCardReadOptions = {
  readCitizenId?: boolean;
  readThaiName?: boolean;
  readEnglishName?: boolean;
  readBirthDate?: boolean;
  readAddress?: boolean;
  readIssueAndExpiryDates?: boolean;
  readPhoto?: boolean;
};

export type ReaderEvent = {
  eventType: "ReaderConnected" | "ReaderDisconnected" | "CardInserted" | "CardRemoved" | "StatusChanged" | "Error";
  readerName: string;
  cardPresent?: boolean | null;
  atr?: string | null;
  occurredAtUtc: string;
};

export type AgentClientOptions = {
  baseUrl?: string;
  timeoutMs?: number;
  signal?: AbortSignal;
};

export class AgentHttpError extends Error {
  constructor(
    public readonly status: number,
    public readonly response: OperationResult<unknown> | AgentError | unknown,
  ) {
    super(`ThaiIdCardAgent request failed with HTTP ${status}`);
    this.name = "AgentHttpError";
  }
}

const defaultBaseUrl = "https://127.0.0.1:18443";

export async function getAgentHealth(options: AgentClientOptions = {}): Promise<AgentHealth> {
  return request<AgentHealth>("/api/v1/health", { ...options, auth: null });
}

export async function getReaders(
  tokenOrDevelopmentKey: AgentCredential,
  options: AgentClientOptions = {},
): Promise<OperationResult<SmartCardReaderInfo[]>> {
  return request<OperationResult<SmartCardReaderInfo[]>>("/api/v1/readers", { ...options, auth: tokenOrDevelopmentKey });
}

export async function getCardStatus(
  tokenOrDevelopmentKey: AgentCredential,
  readerName?: string,
  options: AgentClientOptions = {},
): Promise<OperationResult<SmartCardStatus>> {
  const path = new URL(`${resolveBaseUrl(options.baseUrl)}/api/v1/card/status`);
  if (readerName) path.searchParams.set("readerName", readerName);
  return requestAbsolute<OperationResult<SmartCardStatus>>(path.toString(), { ...options, auth: tokenOrDevelopmentKey });
}

export async function readCardAtr(
  tokenOrDevelopmentKey: AgentCredential,
  readerName?: string,
  options: AgentClientOptions = {},
): Promise<OperationResult<CardAtrResponse>> {
  return request<OperationResult<CardAtrResponse>>("/api/v1/card/atr", {
    ...options,
    auth: tokenOrDevelopmentKey,
    method: "POST",
    body: { readerName: readerName ?? null, requestId: crypto.randomUUID() },
  });
}

export async function readThaiIdCard(
  tokenOrDevelopmentKey: AgentCredential,
  readOptions: ThaiIdCardReadOptions,
  readerName?: string,
  options: AgentClientOptions = {},
): Promise<OperationResult<never>> {
  return request<OperationResult<never>>("/api/v1/card/read", {
    ...options,
    auth: tokenOrDevelopmentKey,
    method: "POST",
    body: { readerName: readerName ?? null, options: readOptions, requestId: crypto.randomUUID() },
  });
}

export function subscribeReaderEvents(
  tokenOrDevelopmentKey: AgentCredential,
  handlers: {
    onEvent: (event: ReaderEvent) => void;
    onError?: (error: unknown) => void;
  },
  options: AgentClientOptions = {},
): () => void {
  const controller = new AbortController();
  const outerSignal = options.signal;
  if (outerSignal) {
    if (outerSignal.aborted) controller.abort();
    else outerSignal.addEventListener("abort", () => controller.abort(), { once: true });
  }

  void readEventStream(tokenOrDevelopmentKey, handlers, { ...options, signal: controller.signal });
  return () => controller.abort();
}

async function readEventStream(
  tokenOrDevelopmentKey: AgentCredential,
  handlers: { onEvent: (event: ReaderEvent) => void; onError?: (error: unknown) => void },
  options: AgentClientOptions,
): Promise<void> {
  try {
    const response = await fetch(`${resolveBaseUrl(options.baseUrl)}/api/v1/events`, {
      method: "GET",
      headers: authHeaders(tokenOrDevelopmentKey),
      signal: options.signal,
      cache: "no-store",
    });
    if (!response.ok) throw new AgentHttpError(response.status, await readJsonSafely(response));
    if (!response.body) return;

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const parts = buffer.split("\n\n");
      buffer = parts.pop() ?? "";
      for (const part of parts) {
        const dataLine = part.split("\n").find((line) => line.startsWith("data: "));
        if (!dataLine) continue;
        handlers.onEvent(JSON.parse(dataLine.slice(6)) as ReaderEvent);
      }
    }
  } catch (error) {
    if ((error as DOMException).name !== "AbortError") handlers.onError?.(error);
  }
}

type RequestOptions = AgentClientOptions & {
  auth?: AgentCredential | null;
  method?: string;
  body?: unknown;
};

async function request<T>(path: string, options: RequestOptions): Promise<T> {
  return requestAbsolute<T>(`${resolveBaseUrl(options.baseUrl)}${path}`, options);
}

async function requestAbsolute<T>(url: string, options: RequestOptions): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), options.timeoutMs ?? 10_000);
  const outerSignal = options.signal;
  if (outerSignal) {
    if (outerSignal.aborted) controller.abort();
    else outerSignal.addEventListener("abort", () => controller.abort(), { once: true });
  }

  try {
    const response = await fetch(url, {
      method: options.method ?? "GET",
      signal: controller.signal,
      headers: {
        ...(options.auth ? authHeaders(options.auth) : {}),
        ...(options.body ? { "Content-Type": "application/json" } : {}),
      },
      body: options.body ? JSON.stringify(options.body) : undefined,
      cache: "no-store",
    });
    const value = await readJsonSafely(response);
    if (!response.ok) throw new AgentHttpError(response.status, value);
    return value as T;
  } finally {
    clearTimeout(timeout);
  }
}

async function readJsonSafely(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) return null;
  return JSON.parse(text) as unknown;
}

function authHeaders(credential: AgentCredential): Record<string, string> {
  const resolved = typeof credential === "string" ? inferCredential(credential) : credential;
  return resolved.type === "bearerToken"
    ? { Authorization: `Bearer ${resolved.value}` }
    : { "X-Agent-Development-Key": resolved.value };
}

function inferCredential(value: string): Exclude<AgentCredential, string> {
  return value.split(".").length === 3
    ? { type: "bearerToken", value }
    : { type: "developmentKey", value };
}

function resolveBaseUrl(baseUrl?: string): string {
  return (baseUrl ?? defaultBaseUrl).replace(/\/$/, "");
}
