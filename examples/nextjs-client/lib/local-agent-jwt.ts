import { createPrivateKey, randomUUID, sign } from "node:crypto";
import { readFileSync } from "node:fs";

export type LocalAgentJwtOptions = {
  issuer?: string;
  audience?: string;
  subject?: string;
  workstationId?: string;
  ttlSeconds?: number;
  privateKeyPem?: string;
  privateKeyPath?: string;
  now?: Date;
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

  const now = options.now ?? new Date();
  const iat = Math.floor(now.getTime() / 1000);
  const exp = iat + ttlSeconds;
  const payload = {
    iss: options.issuer ?? defaultIssuer,
    aud: options.audience ?? defaultAudience,
    sub: options.subject ?? defaultSubject,
    workstation_id: options.workstationId ?? defaultWorkstationId,
    jti: randomUUID(),
    nbf: iat,
    iat,
    exp,
  };
  const header = { alg: "RS256", typ: "JWT" };
  const signingInput = `${base64urlJson(header)}.${base64urlJson(payload)}`;
  const keyObject = createPrivateKey(privateKeyPem);
  const signature = sign("RSA-SHA256", Buffer.from(signingInput, "utf8"), keyObject);
  return {
    token: `${signingInput}.${base64url(signature)}`,
    expiresAtUtc: new Date(exp * 1000).toISOString(),
  };
}

export function issueLocalAgentJwtFromEnvironment(env: Record<string, string | undefined> = process.env): IssuedLocalAgentJwt {
  return issueLocalAgentJwt({
    issuer: env.THAI_ID_AGENT_JWT_ISSUER,
    audience: env.THAI_ID_AGENT_JWT_AUDIENCE,
    subject: env.THAI_ID_AGENT_JWT_SUBJECT,
    workstationId: env.THAI_ID_AGENT_WORKSTATION_ID,
    ttlSeconds: parseTtl(env.THAI_ID_AGENT_JWT_TTL_SECONDS),
    privateKeyPem: env.THAI_ID_AGENT_JWT_PRIVATE_KEY_PEM,
    privateKeyPath: env.THAI_ID_AGENT_JWT_PRIVATE_KEY_PATH,
  });
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

