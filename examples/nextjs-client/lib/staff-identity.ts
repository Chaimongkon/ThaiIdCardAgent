/**
 * Authenticated staff identity for member verification.
 *
 * ## The rule this module exists to enforce
 *
 * The operator recorded in an audit record must be the operator who is actually signed in. Staff
 * identity is therefore **never** taken from a request header, request body, query parameter,
 * `localStorage`, or `sessionStorage` — every one of those is chosen by the client, so a client
 * could attribute its own action to somebody else and the audit trail would be worthless.
 *
 * The only accepted source is a session token the server itself signed and can verify.
 */

import { createHmac, timingSafeEqual } from "node:crypto";

export type StaffIdentity = {
  /** Stable identifier of the signed-in operator. */
  staffId: string;
  /** Optional display name, for the UI. Never used for authorization. */
  displayName?: string;
  /** Department or branch the operator belongs to. */
  department?: string;
  /** Workstation / counter the action was performed at. */
  workstationId: string;
};

export type StaffIdentityResolution =
  | { authenticated: true; identity: StaffIdentity }
  | { authenticated: false; reason: StaffIdentityFailureReason };

export type StaffIdentityFailureReason =
  | "NO_SESSION"
  | "SESSION_MALFORMED"
  | "SESSION_SIGNATURE_INVALID"
  | "SESSION_EXPIRED"
  | "SESSION_INCOMPLETE"
  | "NOT_CONFIGURED";

export interface StaffIdentityProvider {
  resolve(request: Request): Promise<StaffIdentityResolution>;
}

/** Header and body field names that must never be trusted as a source of staff identity. */
export const ClientControlledIdentityFields = [
  "x-staff-id",
  "x-staff-identifier",
  "x-user-id",
  "x-operator-id",
  "x-workstation-id",
  "staffId",
  "staffIdentifier",
  "operatorId",
] as const;

/**
 * Default provider: refuses everything.
 *
 * A deployment that has not wired its real staff authentication gets "not authenticated", never a
 * fallback identity. An unauthenticated verification must fail rather than be attributed to a
 * placeholder operator.
 */
export class UnconfiguredStaffIdentityProvider implements StaffIdentityProvider {
  async resolve(): Promise<StaffIdentityResolution> {
    return { authenticated: false, reason: "NOT_CONFIGURED" };
  }
}

// ---------------------------------------------------------------------------------------------
// Signed session cookie provider
// ---------------------------------------------------------------------------------------------

export type SignedSessionOptions = {
  /** Server-side secret. Never exposed to the browser, never a NEXT_PUBLIC_ variable. */
  secret: string;
  /** Cookie carrying the session token. Should be HttpOnly, Secure, SameSite=Strict. */
  cookieName?: string;
  now?: () => Date;
};

type SessionPayload = {
  staffId?: unknown;
  displayName?: unknown;
  department?: unknown;
  workstationId?: unknown;
  exp?: unknown;
};

/**
 * Verifies a session token of the form `base64url(payload).base64url(hmacSha256)`.
 *
 * The client transmits this cookie, but cannot forge one: the signature requires a secret only the
 * server holds. That is what makes it a legitimate source of identity while a plain header is not.
 *
 * This is a self-contained reference implementation. A deployment with existing SSO should
 * implement `StaffIdentityProvider` against that system instead — the rest of the flow is unchanged.
 */
export class SignedSessionStaffIdentityProvider implements StaffIdentityProvider {
  private readonly cookieName: string;
  private readonly now: () => Date;

  constructor(private readonly options: SignedSessionOptions) {
    this.cookieName = options.cookieName ?? "coop_staff_session";
    this.now = options.now ?? (() => new Date());
  }

  async resolve(request: Request): Promise<StaffIdentityResolution> {
    if (!this.options.secret) {
      return { authenticated: false, reason: "NOT_CONFIGURED" };
    }

    const token = readCookie(request.headers.get("cookie"), this.cookieName);
    if (!token) {
      return { authenticated: false, reason: "NO_SESSION" };
    }

    const separator = token.lastIndexOf(".");
    if (separator <= 0 || separator === token.length - 1) {
      return { authenticated: false, reason: "SESSION_MALFORMED" };
    }

    const encodedPayload = token.slice(0, separator);
    const providedSignature = token.slice(separator + 1);
    const expectedSignature = signPayload(encodedPayload, this.options.secret);

    if (!constantTimeEquals(providedSignature, expectedSignature)) {
      return { authenticated: false, reason: "SESSION_SIGNATURE_INVALID" };
    }

    let payload: SessionPayload;
    try {
      payload = JSON.parse(Buffer.from(encodedPayload, "base64url").toString("utf8")) as SessionPayload;
    } catch {
      return { authenticated: false, reason: "SESSION_MALFORMED" };
    }

    if (typeof payload.exp === "number" && payload.exp * 1000 <= this.now().getTime()) {
      return { authenticated: false, reason: "SESSION_EXPIRED" };
    }

    const staffId = asNonEmptyString(payload.staffId);
    const workstationId = asNonEmptyString(payload.workstationId);
    if (!staffId || !workstationId) {
      return { authenticated: false, reason: "SESSION_INCOMPLETE" };
    }

    return {
      authenticated: true,
      identity: {
        staffId,
        workstationId,
        displayName: asNonEmptyString(payload.displayName) ?? undefined,
        department: asNonEmptyString(payload.department) ?? undefined,
      },
    };
  }
}

/**
 * Issues a session token. Present so the reference provider is testable and so a deployment can see
 * the expected token shape; a real deployment issues these from its own sign-in flow.
 */
export function issueStaffSessionToken(
  identity: StaffIdentity & { expiresAtUnixSeconds: number },
  secret: string,
): string {
  const payload = {
    staffId: identity.staffId,
    displayName: identity.displayName,
    department: identity.department,
    workstationId: identity.workstationId,
    exp: identity.expiresAtUnixSeconds,
  };
  const encoded = Buffer.from(JSON.stringify(payload), "utf8").toString("base64url");
  return `${encoded}.${signPayload(encoded, secret)}`;
}

function signPayload(encodedPayload: string, secret: string): string {
  return createHmac("sha256", secret).update(encodedPayload, "utf8").digest("base64url");
}

function constantTimeEquals(left: string, right: string): boolean {
  const leftBytes = Buffer.from(left, "utf8");
  const rightBytes = Buffer.from(right, "utf8");
  if (leftBytes.length !== rightBytes.length) return false;
  return timingSafeEqual(leftBytes, rightBytes);
}

function readCookie(cookieHeader: string | null, name: string): string | null {
  if (!cookieHeader) return null;
  for (const part of cookieHeader.split(";")) {
    const separator = part.indexOf("=");
    if (separator === -1) continue;
    if (part.slice(0, separator).trim() === name) {
      return decodeURIComponent(part.slice(separator + 1).trim());
    }
  }
  return null;
}

function asNonEmptyString(value: unknown): string | null {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : null;
}
