export type LocalAgentTokenResponse = {
  token: string;
  expiresAtUtc: string;
};

export async function getFreshLocalAgentToken(fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl("/api/local-agent/token", {
    method: "POST",
    headers: { Accept: "application/json" },
    cache: "no-store",
  });

  if (!response.ok) {
    throw new Error("Cannot issue a short-lived local Agent token.");
  }

  const payload = (await response.json()) as Partial<LocalAgentTokenResponse>;
  if (!payload.token || typeof payload.token !== "string") {
    throw new Error("Token broker returned an invalid response.");
  }

  return payload.token;
}
