import { createPrivateKey, randomUUID, sign } from "node:crypto";
import { readFileSync } from "node:fs";

/**
 * Permissions the Agent understands.
 *
 * The Agent reads permissions from the `scope` claim (space-delimited) — see
 * `AgentPermissionClaims.FromPrincipalClaims` in
 * `src/ThaiIdCardAgent.Service/CardReadAuthorization.cs`. The literal below must stay in step with
 * `AgentPermissions.CardRead` on that side; `tests/token-broker.test.ts` pins the claim name and
 * value, and `CardReadEndpointTests` pins the Agent's half of the same contract.
 */
export const AgentPermission = {
  /** Required by POST /api/v1/card/read. Granted only to tokens minted for a card read. */
  CardRead: "card.read",
} as const;

export type AgentPermissionValue = (typeof AgentPermission)[keyof typeof AgentPermission];

/** Every permission the broker is willing to mint. Anything else is refused. */
const grantablePermissions: readonly string[] = [AgentPermission.CardRead];

export type LocalAgentJwtOptions = {
  issuer?: string;
  audience?: string;
  subject?: string;
  workstationId?: string;
  ttlSeconds?: number;
  privateKeyPem?: string;
  privateKeyPath?: string;
  now?: Date;
  /**
   * Permissions to embed in the `scope` claim. Empty/omitted mints a token that can read reader and
   * card status but **cannot** read a card — least privilege is the default, and `card.read` is
   * granted only when a card read is actually being performed.
   */
  permissions?: readonly string[];
};

export type IssuedLocalAgentJwt = {
  token: string;
  expiresAtUtc: string;
};

const defaultIssuer = "thai-id-card-agent-client";
const defaultAudience = "thai-id-card-agent";
const defaultSubject = "nextjs-client";
const defaultWorkstationId = "localhost-pilot";

export function issueLocalAgentJwt(options: LocalAgentJwtOptions): IssuedLocalAgentJwt {
  const privateKeyPem = resolvePrivateKeyPem(options);
  const ttlSeconds = options.ttlSeconds ?? 60;
  if (!Number.isInteger(ttlSeconds) || ttlSeconds < 1 || ttlSeconds > 60) {
    throw new Error("JWT ttlSeconds must be between 1 and 60 seconds.");
  }

  const permissions = normalizePermissions(options.permissions);

  const now = options.now ?? new Date();
  const iat = Math.floor(now.getTime() / 1000);
  const exp = iat + ttlSeconds;
  const payload: Record<string, unknown> = {
    iss: options.issuer ?? defaultIssuer,
    aud: options.audience ?? defaultAudience,
    sub: options.subject ?? defaultSubject,
    workstation_id: options.workstationId ?? defaultWorkstationId,
    jti: randomUUID(),
    nbf: iat,
    iat,
    exp,
  };
  // The claim is omitted entirely when no permission was requested, so a status-only token carries
  // no permission surface at all rather than an empty one.
  if (permissions.length > 0) {
    payload.scope = permissions.join(" ");
  }
  const header = { alg: "RS256", typ: "JWT" };
  const signingInput = `${base64urlJson(header)}.${base64urlJson(payload)}`;
  const keyObject = createPrivateKey(privateKeyPem);
  const signature = sign("RSA-SHA256", Buffer.from(signingInput, "utf8"), keyObject);
  return {
    token: `${signingInput}.${base64url(signature)}`,
    expiresAtUtc: new Date(exp * 1000).toISOString(),
  };
}

export function issueLocalAgentJwtFromEnvironment(
  env: Record<string, string | undefined> = process.env,
  permissions: readonly string[] = [],
): IssuedLocalAgentJwt {
  return issueLocalAgentJwt({
    issuer: env.THAI_ID_AGENT_JWT_ISSUER,
    audience: env.THAI_ID_AGENT_JWT_AUDIENCE,
    subject: env.THAI_ID_AGENT_JWT_SUBJECT,
    workstationId: env.THAI_ID_AGENT_WORKSTATION_ID,
    ttlSeconds: parseTtl(env.THAI_ID_AGENT_JWT_TTL_SECONDS),
    privateKeyPem: env.THAI_ID_AGENT_JWT_PRIVATE_KEY_PEM,
    privateKeyPath: env.THAI_ID_AGENT_JWT_PRIVATE_KEY_PATH,
    permissions,
  });
}

/**
 * Validates the requested permissions against the grantable set. An unknown permission is refused
 * rather than passed through, so a caller cannot widen its own token by inventing a scope string.
 */
function normalizePermissions(requested: readonly string[] | undefined): string[] {
  if (!requested || requested.length === 0) return [];
  const unique = Array.from(new Set(requested.map((permission) => permission.trim()).filter(Boolean)));
  for (const permission of unique) {
    if (!grantablePermissions.includes(permission)) {
      throw new Error(`Refusing to mint an unknown Agent permission: '${permission}'.`);
    }
  }
  return unique.sort();
}

function resolvePrivateKeyPem(options: LocalAgentJwtOptions): string {
  if (options.privateKeyPem && options.privateKeyPem.trim().length > 0) return options.privateKeyPem;
  if (options.privateKeyPath && options.privateKeyPath.trim().length > 0) {
    if (/[<>]/.test(options.privateKeyPath)) throw new Error("JWT private key path contains a placeholder.");
    return readFileSync(options.privateKeyPath, "utf8");
  }

  throw new Error("JWT private key is not configured. Set THAI_ID_AGENT_JWT_PRIVATE_KEY_PATH on the Next.js server.");
}

function parseTtl(value: string | undefined): number | undefined {
  if (!value) return undefined;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function base64urlJson(value: unknown): string {
  return base64url(Buffer.from(JSON.stringify(value), "utf8"));
}

function base64url(value: Buffer): string {
  return value.toString("base64").replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_");
}

