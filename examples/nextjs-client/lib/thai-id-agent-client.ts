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

export type ReaderEventType = "ReaderConnected" | "ReaderDisconnected" | "CardInserted" | "CardRemoved" | "StatusChanged" | "Error";

export type ReaderEvent = {
  eventType: ReaderEventType;
  readerName: string;
  cardPresent?: boolean | null;
  atr?: string | null;
  occurredAtUtc: string;
};

export type AgentClientOptions = {
  baseUrl?: string;
  timeoutMs?: number;
  fetchImpl?: typeof fetch;
  getToken: () => Promise<string>;
};

export type PublicAgentClientOptions = Omit<AgentClientOptions, "getToken"> & {
  getToken?: () => Promise<string>;
};

export type AgentFailureKind = "tls-or-network" | "timeout" | "auth" | "replay" | "card-not-present" | "agent" | "invalid-response";

export class AgentClientError extends Error {
  constructor(
    public readonly kind: AgentFailureKind,
    message: string,
    public readonly status?: number,
    public readonly code?: string,
    public readonly requestId?: string | null,
  ) {
    super(message);
    this.name = "AgentClientError";
  }
}

const defaultBaseUrl = "https://localhost:18443";
const defaultTimeoutMs = 10_000;

export function createThaiIdAgentClient(options: AgentClientOptions) {
  return {
    getHealth: (overrides: PublicAgentClientOptions = {}) => getAgentHealth({ ...options, ...overrides }),
    getReaders: (overrides: PublicAgentClientOptions = {}) => getReaders({ ...options, ...overrides }),
    getCardStatus: (readerName?: string, overrides: PublicAgentClientOptions = {}) => getCardStatus(readerName, { ...options, ...overrides }),
    readCardAtr: (readerName?: string, overrides: PublicAgentClientOptions = {}) => readCardAtr(readerName, { ...options, ...overrides }),
  };
}

export async function getAgentHealth(options: PublicAgentClientOptions = {}): Promise<AgentHealth> {
  return requestJson<AgentHealth>("GET", "/api/v1/health", { ...options, getToken: undefined });
}

export async function getReaders(options: AgentClientOptions): Promise<OperationResult<SmartCardReaderInfo[]>> {
  return requestJson<OperationResult<SmartCardReaderInfo[]>>("GET", "/api/v1/readers", options);
}

export async function getCardStatus(readerName: string | undefined, options: AgentClientOptions): Promise<OperationResult<SmartCardStatus>> {
  const path = new URL(`${resolveBaseUrl(options.baseUrl)}/api/v1/card/status`);
  if (readerName) path.searchParams.set("readerName", readerName);
  return requestJsonAbsolute<OperationResult<SmartCardStatus>>("GET", path.toString(), options);
}

export async function readCardAtr(readerName: string | undefined, options: AgentClientOptions): Promise<OperationResult<CardAtrResponse>> {
  return requestJson<OperationResult<CardAtrResponse>>("POST", "/api/v1/card/atr", options, {
    readerName: readerName ?? null,
    requestId: createRequestId(),
  });
}

export function normalizeAtr(value: string | null | undefined): string | null {
  if (!value) return null;
  const normalized = value.toUpperCase();
  return /^[0-9A-F]{2}(?:-[0-9A-F]{2})*$/.test(normalized) ? normalized : null;
}

export function classifyErrorPayload(status: number, payload: unknown): AgentClientError {
  const result = isOperationResult(payload) ? payload : null;
  const code = result?.error?.code;
  const requestId = result?.requestId ?? null;
  if (status === 401 || status === 403) {
    const kind = code === "UNAUTHORIZED" && result?.error?.message?.toLowerCase().includes("replay") ? "replay" : "auth";
    return new AgentClientError(kind, "Agent authentication failed.", status, code, requestId);
  }

  if (code === "CARD_NOT_PRESENT") {
    return new AgentClientError("card-not-present", "No smart card is present in the selected reader.", status, code, requestId);
  }

  return new AgentClientError("agent", result?.error?.message || `ThaiIdCardAgent returned HTTP ${status}.`, status, code, requestId);
}

async function requestJson<T>(method: string, path: string, options: PublicAgentClientOptions, body?: unknown): Promise<T> {
  return requestJsonAbsolute<T>(method, `${resolveBaseUrl(options.baseUrl)}${path}`, options, body);
}

async function requestJsonAbsolute<T>(method: string, url: string, options: PublicAgentClientOptions, body?: unknown): Promise<T> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), options.timeoutMs ?? defaultTimeoutMs);
  const headers: Record<string, string> = { Accept: "application/json" };

  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (options.getToken) headers.Authorization = `Bearer ${await options.getToken()}`;

  try {
    const response = await fetchImpl(url, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: controller.signal,
      cache: "no-store",
    });
    const payload = await readJsonSafely(response);
    if (!response.ok) throw classifyErrorPayload(response.status, payload);
    return payload as T;
  } catch (error) {
    if (error instanceof AgentClientError) throw error;
    if (isAbortError(error)) throw new AgentClientError("timeout", "ThaiIdCardAgent request timed out.");
    throw new AgentClientError("tls-or-network", "Cannot connect to ThaiIdCardAgent. Check the service and HTTPS certificate trust.");
  } finally {
    clearTimeout(timeout);
  }
}

async function readJsonSafely(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    throw new AgentClientError("invalid-response", "ThaiIdCardAgent returned a non-JSON response.", response.status);
  }
}

function isOperationResult(value: unknown): value is OperationResult<unknown> {
  return typeof value === "object" && value !== null && "success" in value && "error" in value;
}

function isAbortError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "name" in error && (error as { name?: string }).name === "AbortError";
}

function createRequestId(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
  return `req-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

export function resolveBaseUrl(baseUrl?: string): string {
  return (baseUrl ?? defaultBaseUrl).replace(/\/$/, "");
}
