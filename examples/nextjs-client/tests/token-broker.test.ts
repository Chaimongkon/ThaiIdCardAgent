import { generateKeyPairSync, verify } from "node:crypto";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { issueLocalAgentJwt, issueLocalAgentJwtFromEnvironment } from "@/lib/local-agent-jwt";

function keys() {
  return generateKeyPairSync("rsa", {
    modulusLength: 2048,
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
    publicKeyEncoding: { type: "spki", format: "pem" },
  });
}

function decodePayload(token: string) {
  return JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString("utf8")) as Record<string, unknown>;
}

describe("local Agent token broker", () => {
  it("issues an RS256 token with the Agent issuer, audience, required claims, and <= 60 second lifetime", () => {
    const { privateKey, publicKey } = keys();
    const issued = issueLocalAgentJwt({ privateKeyPem: privateKey, now: new Date("2026-08-04T00:00:00Z") });
    const [header, payload, signature] = issued.token.split(".");
    const claims = decodePayload(issued.token);

    expect(JSON.parse(Buffer.from(header, "base64url").toString("utf8"))).toMatchObject({ alg: "RS256", typ: "JWT" });
    expect(claims.iss).toBe("thai-id-card-agent-client");
    expect(claims.aud).toBe("thai-id-card-agent");
    expect(claims.sub).toBe("nextjs-client");
    expect(claims.workstation_id).toBe("localhost-pilot");
    expect(Number(claims.exp) - Number(claims.nbf)).toBeLessThanOrEqual(60);
    expect(verify("RSA-SHA256", Buffer.from(`${header}.${payload}`), publicKey, Buffer.from(signature, "base64url"))).toBe(true);
  });

  it("reads the private key from a server-side env path", () => {
    const { privateKey } = keys();
    const dir = mkdtempSync(join(tmpdir(), "thai-id-agent-next-"));
    const keyPath = join(dir, "jwt-private.pem");
    writeFileSync(keyPath, privateKey, "utf8");

    const issued = issueLocalAgentJwtFromEnvironment({ THAI_ID_AGENT_JWT_PRIVATE_KEY_PATH: keyPath });

    expect(issued.token.split(".")).toHaveLength(3);
  });

  it("rejects placeholder private key paths", () => {
    expect(() => issueLocalAgentJwtFromEnvironment({ THAI_ID_AGENT_JWT_PRIVATE_KEY_PATH: "artifacts/test-secrets/<PRIVATE-KEY-FILE>" })).toThrow(/placeholder/i);
  });

  it("rejects token lifetimes longer than 60 seconds", () => {
    const { privateKey } = keys();
    expect(() => issueLocalAgentJwt({ privateKeyPem: privateKey, ttlSeconds: 61 })).toThrow(/between 1 and 60/);
  });
});
