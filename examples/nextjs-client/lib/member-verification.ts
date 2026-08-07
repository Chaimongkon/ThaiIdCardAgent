/**
 * Cooperative member verification.
 *
 * Phase 13A flow: the browser reads only the 13-digit citizen ID from the local agent and posts it
 * to a server-side route. The server matches it against the cooperative member database through
 * `MemberDirectory` and returns member information — never a copy of the citizen ID.
 */

import type { MemberDirectory, MemberRecord } from "./member-directory";

export type VerificationOutcome =
  | "MEMBER_MATCHED"
  | "MEMBER_NOT_FOUND"
  | "MEMBER_DUPLICATE"
  | "MEMBER_DATABASE_UNAVAILABLE"
  | "CITIZEN_ID_INVALID";

/**
 * What the browser is allowed to see.
 *
 * There is deliberately **no `citizenId` field**: after verification the browser holds member
 * identity, not the national identifier. `photoReference` is an identifier for a photo the
 * cooperative system already holds — never image bytes.
 */
export type MemberVerificationResponse = {
  verified: boolean;
  outcome: VerificationOutcome;
  verificationId: string;
  memberId: string | null;
  memberNo: string | null;
  fullName: string | null;
  memberType: string | null;
  memberStatus: string | null;
  photoReference: string | null;
  maskedCitizenId: string | null;
  verifiedAtUtc: string;
};

export type MemberVerificationAuditRecord = {
  verificationId: string;
  timestampUtc: string;
  staffIdentifier: string;
  workstationIdentifier: string;
  department: string | null;
  readerName: string;
  outcome: VerificationOutcome;
  memberId: string | null;
  errorCode: string | null;
  maskedCitizenId: string | null;
  citizenIdCorrelationHash: string | null;
};

export interface VerificationAuditSink {
  write(record: MemberVerificationAuditRecord): Promise<void>;
}

const citizenIdPattern = /^[0-9]{13}$/;

/**
 * Validates a Thai citizen ID: exactly 13 ASCII digits with a correct check digit.
 * Never repairs or normalizes the input — a malformed value is rejected, not fixed.
 */
export function isValidThaiCitizenId(value: string | null | undefined): boolean {
  if (!value || !citizenIdPattern.test(value)) return false;
  let weightedSum = 0;
  for (let index = 0; index < 12; index += 1) {
    weightedSum += Number(value[index]) * (13 - index);
  }
  const expectedCheckDigit = (11 - (weightedSum % 11)) % 10;
  return Number(value[12]) === expectedCheckDigit;
}

/**
 * Masks a citizen ID for display and audit: only the leading and trailing digits survive.
 * Returns null for anything that is not a well-formed citizen ID, so a malformed value can never
 * be partially echoed back.
 */
export function maskCitizenId(value: string | null | undefined): string | null {
  if (!value || !citizenIdPattern.test(value)) return null;
  return `${value[0]}-${value.slice(1, 5)}-xxxxx-${value.slice(10, 12)}-${value[12]}`;
}

/**
 * Keyed correlation hash over a citizen ID, for linking audit records without storing the value.
 *
 * A plain digest of a 13-digit number is reversible by brute force in seconds, so correlation must
 * be keyed. Returns null when no key is configured, so the audit trail simply carries no
 * correlation value rather than a reversible one.
 */
export async function computeCitizenIdCorrelationHash(
  citizenId: string,
  key: string | undefined,
): Promise<string | null> {
  if (!key) return null;
  const { createHmac } = await import("node:crypto");
  return createHmac("sha256", key).update(citizenId, "utf8").digest("hex");
}

export type VerifyMemberInput = {
  citizenId: string;
  verificationId: string;
  readerName: string;
  staffIdentifier: string;
  workstationIdentifier: string;
  department?: string | null;
  directory: MemberDirectory;
  auditSink?: VerificationAuditSink;
  correlationKey?: string;
  now?: Date;
};

/**
 * Runs one verification. Pure orchestration: it holds the citizen ID only long enough to look it
 * up, and returns a response that structurally cannot carry it.
 */
export async function verifyMember(input: VerifyMemberInput): Promise<MemberVerificationResponse> {
  const verifiedAtUtc = (input.now ?? new Date()).toISOString();
  const masked = maskCitizenId(input.citizenId);

  const respond = async (
    outcome: VerificationOutcome,
    member: MemberRecord | null,
    errorCode: string | null,
  ): Promise<MemberVerificationResponse> => {
    await input.auditSink?.write({
      verificationId: input.verificationId,
      timestampUtc: verifiedAtUtc,
      staffIdentifier: input.staffIdentifier,
      workstationIdentifier: input.workstationIdentifier,
      department: input.department ?? null,
      readerName: input.readerName,
      outcome,
      memberId: member?.memberId ?? null,
      errorCode,
      maskedCitizenId: masked,
      citizenIdCorrelationHash: isValidThaiCitizenId(input.citizenId)
        ? await computeCitizenIdCorrelationHash(input.citizenId, input.correlationKey)
        : null,
    });

    return {
      verified: outcome === "MEMBER_MATCHED",
      outcome,
      verificationId: input.verificationId,
      memberId: member?.memberId ?? null,
      memberNo: member?.memberNo ?? null,
      fullName: member?.fullName ?? null,
      memberType: member?.memberType ?? null,
      memberStatus: member?.memberStatus ?? null,
      photoReference: member?.photoReference ?? null,
      maskedCitizenId: masked,
      verifiedAtUtc,
    };
  };

  // Validate before touching the database. A malformed identifier is never looked up and never
  // repaired into a valid one.
  if (!isValidThaiCitizenId(input.citizenId)) {
    return respond("CITIZEN_ID_INVALID", null, "CARD_DATA_INVALID");
  }

  const lookup = await input.directory.lookupByCitizenId(input.citizenId);
  switch (lookup.status) {
    case "Found":
      return respond("MEMBER_MATCHED", lookup.member, null);
    case "NotFound":
      return respond("MEMBER_NOT_FOUND", null, null);
    case "Duplicate":
      // Two members sharing a citizen ID is a data-integrity fault in the member database. Picking
      // one could attach the wrong person to a transaction, so this fails closed and requires
      // manual resolution. The matching rows are never inspected or returned.
      return respond("MEMBER_DUPLICATE", null, "MEMBER_RECORD_DUPLICATE");
    case "DatabaseUnavailable":
      return respond("MEMBER_DATABASE_UNAVAILABLE", null, lookup.errorCode);
  }
}

/** HTTP status for a verification outcome. */
export function statusForOutcome(outcome: VerificationOutcome): number {
  switch (outcome) {
    case "MEMBER_MATCHED":
    case "MEMBER_NOT_FOUND":
      return 200;
    case "MEMBER_DUPLICATE":
      return 409;
    case "CITIZEN_ID_INVALID":
      return 422;
    case "MEMBER_DATABASE_UNAVAILABLE":
      return 503;
  }
}

/** Audit sink that keeps records in memory. Replace with the cooperative audit store. */
export class InMemoryVerificationAuditSink implements VerificationAuditSink {
  readonly records: MemberVerificationAuditRecord[] = [];

  async write(record: MemberVerificationAuditRecord): Promise<void> {
    this.records.push(record);
  }
}
