export type AgentHealth = {
  status: string;
  service: string;
  checkedAtUtc: string;
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
  status: string;
  atr?: string | null;
  checkedAtUtc: string;
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

export type OperationResult<T> = {
  success: boolean;
  data?: T;
  error?: { code: string; message: string };
};

const defaultBaseUrl = "https://127.0.0.1:18443";

export async function getAgentHealth(baseUrl = defaultBaseUrl, timeoutMs = 5000): Promise<AgentHealth> {
  return request<AgentHealth>(`${baseUrl}/api/v1/health`, { timeoutMs });
}

export async function getReaders(token: string, baseUrl = defaultBaseUrl, timeoutMs = 10000): Promise<OperationResult<SmartCardReaderInfo[]>> {
  return request<OperationResult<SmartCardReaderInfo[]>>(`${baseUrl}/api/v1/readers`, { token, timeoutMs });
}

export async function getCardStatus(token: string, readerName?: string, baseUrl = defaultBaseUrl, timeoutMs = 10000): Promise<OperationResult<SmartCardStatus>> {
  const url = new URL(`${baseUrl}/api/v1/card/status`);
  if (readerName) url.searchParams.set("readerName", readerName);
  return request<OperationResult<SmartCardStatus>>(url.toString(), { token, timeoutMs });
}

export async function readCardAtr(token: string, readerName?: string, baseUrl = defaultBaseUrl, timeoutMs = 10000): Promise<OperationResult<{ readerName: string; atr: string; readAtUtc: string }>> {
  return request<OperationResult<{ readerName: string; atr: string; readAtUtc: string }>>(`${baseUrl}/api/v1/card/atr`, {
    method: "POST",
    token,
    timeoutMs,
    body: { readerName },
  });
}

export async function readThaiIdCard(token: string, options: ThaiIdCardReadOptions, readerName?: string, baseUrl = defaultBaseUrl, timeoutMs = 10000): Promise<unknown> {
  return request(`${baseUrl}/api/v1/card/read`, {
    method: "POST",
    token,
    timeoutMs,
    body: { readerName, options },
  });
}

async function request<T>(url: string, options: { method?: string; token?: string; timeoutMs: number; body?: unknown }): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), options.timeoutMs);
  try {
    const response = await fetch(url, {
      method: options.method ?? "GET",
      signal: controller.signal,
      headers: {
        ...(options.token ? { Authorization: `Bearer ${options.token}` } : {}),
        ...(options.body ? { "Content-Type": "application/json" } : {}),
      },
      body: options.body ? JSON.stringify(options.body) : undefined,
      cache: "no-store",
    });
    const value = (await response.json()) as T;
    if (!response.ok) throw value;
    return value;
  } finally {
    clearTimeout(timeout);
  }
}