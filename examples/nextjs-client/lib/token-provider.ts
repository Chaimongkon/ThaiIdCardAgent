export type LocalAgentTokenResponse = {
  token: string;
  expiresAtUtc: string;
  purpose?: string;
};

/** Purposes the broker supports. `status` carries no permission; `card-read` grants `card.read`. */
export type LocalAgentTokenPurpose = "status" | "card-read";

async function requestToken(purpose: LocalAgentTokenPurpose, fetchImpl: typeof fetch): Promise<string> {
  const response = await fetchImpl("/api/local-agent/token", {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    cache: "no-store",
    body: JSON.stringify({ purpose }),
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

/**
 * Least-privilege token for reader/card status and events. Carries no permission claim, so it
 * cannot be used against POST /api/v1/card/read.
 */
export async function getFreshLocalAgentToken(fetchImpl: typeof fetch = fetch): Promise<string> {
  return requestToken("status", fetchImpl);
}

/**
 * Token carrying `card.read`. Requested only at the moment an operator triggers a card read, so the
 * permission is never present on the routine status/SSE traffic.
 */
export async function getCardReadToken(fetchImpl: typeof fetch = fetch): Promise<string> {
  return requestToken("card-read", fetchImpl);
}
