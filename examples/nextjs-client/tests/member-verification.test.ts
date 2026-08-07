import { describe, expect, it } from "vitest";
import {
  InMemoryVerificationAuditSink,
  computeCitizenIdCorrelationHash,
  isValidThaiCitizenId,
  maskCitizenId,
  statusForOutcome,
  verifyMember,
} from "@/lib/member-verification";
import { MockMemberDirectory, SyntheticCitizenIds } from "@/lib/member-directory-mock";
import { NotConfiguredMemberDirectory, type MemberDirectory } from "@/lib/member-directory";

/**
 * Every citizen ID here is a synthetic, checksum-valid value. No real citizen ID appears in this
 * repository.
 */
const validId = SyntheticCitizenIds.activeMember;

function baseInput(overrides: Partial<Parameters<typeof verifyMember>[0]> = {}) {
  return {
    citizenId: validId,
    verificationId: "v-1",
    readerName: "Reader A",
    staffIdentifier: "operator-1",
    workstationIdentifier: "counter-1",
    directory: new MockMemberDirectory(),
    now: new Date("2026-08-06T00:00:00Z"),
    ...overrides,
  };
}

describe("isValidThaiCitizenId", () => {
  it("accepts synthetic checksum-valid ids", () => {
    expect(isValidThaiCitizenId(validId)).toBe(true);
    expect(isValidThaiCitizenId(SyntheticCitizenIds.duplicatedMember)).toBe(true);
  });

  it("enforces the check digit", () => {
    const body = validId.slice(0, 12);
    const correct = validId[12];
    for (let digit = 0; digit <= 9; digit += 1) {
      expect(isValidThaiCitizenId(body + String(digit))).toBe(String(digit) === correct);
    }
  });

  it("rejects malformed values without repairing them", () => {
    expect(isValidThaiCitizenId("110170020736")).toBe(false);
    expect(isValidThaiCitizenId("11017002073666")).toBe(false);
    expect(isValidThaiCitizenId("110170020736X")).toBe(false);
    expect(isValidThaiCitizenId("1-1017-00207-36-6")).toBe(false);
    expect(isValidThaiCitizenId(null)).toBe(false);
    expect(isValidThaiCitizenId(undefined)).toBe(false);
  });

  it("matches the .NET validator on the same inputs", () => {
    // The agent validates before returning and the server validates before lookup; the two must
    // agree, otherwise one layer would accept what the other rejects.
    expect(isValidThaiCitizenId("1101700207360")).toBe(false);
    expect(isValidThaiCitizenId("2123456789012")).toBe(true);
  });
});

describe("maskCitizenId", () => {
  it("hides the middle digits", () => {
    const masked = maskCitizenId(validId);
    expect(masked).not.toContain(validId);
    expect(masked).toContain("xxxxx");
    expect(masked).not.toContain(validId.slice(5, 10));
  });

  it("returns null rather than partially echoing a malformed value", () => {
    expect(maskCitizenId("110170020736X")).toBeNull();
    expect(maskCitizenId("123")).toBeNull();
    expect(maskCitizenId(null)).toBeNull();
  });
});

describe("computeCitizenIdCorrelationHash", () => {
  it("returns null when no key is configured, rather than an unkeyed digest", () => {
    // An unkeyed hash of a 13-digit number is reversible by brute force, so no hash is better.
    return expect(computeCitizenIdCorrelationHash(validId, undefined)).resolves.toBeNull();
  });

  it("is stable for the same key and differs across keys", async () => {
    const a1 = await computeCitizenIdCorrelationHash(validId, "key-a");
    const a2 = await computeCitizenIdCorrelationHash(validId, "key-a");
    const b1 = await computeCitizenIdCorrelationHash(validId, "key-b");
    expect(a1).toBe(a2);
    expect(a1).not.toBe(b1);
    expect(a1).not.toContain(validId);
  });
});

describe("verifyMember", () => {
  it("returns member data when exactly one member matches", async () => {
    const result = await verifyMember(baseInput());

    expect(result.verified).toBe(true);
    expect(result.outcome).toBe("MEMBER_MATCHED");
    expect(result.memberId).toBe("MOCK-M-0001");
    expect(result.memberNo).toBe("000001");
    expect(result.memberType).toBe("สามัญ");
    expect(result.memberStatus).toBe("ปกติ");
  });

  it("returns the photo reference when the cooperative system has one", async () => {
    const result = await verifyMember(baseInput({ citizenId: SyntheticCitizenIds.activeMemberWithPhoto }));

    expect(result.photoReference).toBe("mock-photo/000002");
  });

  it("never returns the raw citizen id", async () => {
    const result = await verifyMember(baseInput());

    expect(JSON.stringify(result)).not.toContain(validId);
    expect(result).not.toHaveProperty("citizenId");
    expect(result.maskedCitizenId).toContain("xxxxx");
  });

  it("reports member not found without leaking whether the id exists elsewhere", async () => {
    const result = await verifyMember(baseInput({ citizenId: SyntheticCitizenIds.unknownMember }));

    expect(result.verified).toBe(false);
    expect(result.outcome).toBe("MEMBER_NOT_FOUND");
    expect(result.memberId).toBeNull();
    expect(result.fullName).toBeNull();
  });

  it("surfaces an inactive member with its status", async () => {
    const result = await verifyMember(baseInput({ citizenId: SyntheticCitizenIds.inactiveMember }));

    expect(result.outcome).toBe("MEMBER_MATCHED");
    expect(result.memberStatus).toBe("ลาออก");
  });

  it("fails closed when two members share a citizen id", async () => {
    // Picking one of them could attach the wrong person to a transaction.
    const result = await verifyMember(baseInput({ citizenId: SyntheticCitizenIds.duplicatedMember }));

    expect(result.verified).toBe(false);
    expect(result.outcome).toBe("MEMBER_DUPLICATE");
    expect(result.memberId).toBeNull();
    expect(result.fullName).toBeNull();
    expect(result.memberType).toBeNull();
    expect(result.memberStatus).toBeNull();
  });

  it("reports the database being unavailable rather than reporting not-found", async () => {
    const result = await verifyMember(
      baseInput({ directory: new MockMemberDirectory(undefined, { failWith: "MEMBER_DB_UNAVAILABLE" }) }),
    );

    expect(result.verified).toBe(false);
    expect(result.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
  });

  it("distinguishes an unconfigured directory from a genuine miss", async () => {
    const result = await verifyMember(baseInput({ directory: new NotConfiguredMemberDirectory() }));

    expect(result.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
    expect(result.outcome).not.toBe("MEMBER_NOT_FOUND");
  });

  it("reports a database timeout as unavailable", async () => {
    const result = await verifyMember(
      baseInput({ directory: new MockMemberDirectory(undefined, { failWith: "MEMBER_DB_TIMEOUT" }) }),
    );

    expect(result.outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
  });

  it("rejects a malformed citizen id before any database lookup", async () => {
    let lookups = 0;
    const directory: MemberDirectory = {
      async lookupByCitizenId() {
        lookups += 1;
        return { status: "NotFound" };
      },
    };

    const result = await verifyMember(baseInput({ citizenId: "110170020736X", directory }));

    expect(result.outcome).toBe("CITIZEN_ID_INVALID");
    expect(lookups).toBe(0);
  });

  it("writes an audit record for a match, with no raw citizen id", async () => {
    const auditSink = new InMemoryVerificationAuditSink();
    await verifyMember(baseInput({ auditSink, correlationKey: "audit-key", department: "สาขาทดสอบ" }));

    expect(auditSink.records).toHaveLength(1);
    const record = auditSink.records[0];
    expect(record.verificationId).toBe("v-1");
    expect(record.staffIdentifier).toBe("operator-1");
    expect(record.workstationIdentifier).toBe("counter-1");
    expect(record.department).toBe("สาขาทดสอบ");
    expect(record.readerName).toBe("Reader A");
    expect(record.outcome).toBe("MEMBER_MATCHED");
    expect(record.memberId).toBe("MOCK-M-0001");
    expect(record.maskedCitizenId).toContain("xxxxx");
    expect(record.citizenIdCorrelationHash).toBeTruthy();
    expect(JSON.stringify(record)).not.toContain(validId);
  });

  it("writes an audit record with a sanitized error code when the database fails", async () => {
    const auditSink = new InMemoryVerificationAuditSink();
    await verifyMember(
      baseInput({ auditSink, directory: new MockMemberDirectory(undefined, { failWith: "MEMBER_DB_QUERY_FAILED" }) }),
    );

    expect(auditSink.records[0].outcome).toBe("MEMBER_DATABASE_UNAVAILABLE");
    expect(auditSink.records[0].errorCode).toBe("MEMBER_DB_QUERY_FAILED");
    expect(auditSink.records[0].memberId).toBeNull();
  });

  it("records a duplicate with its own error code and no member id", async () => {
    const auditSink = new InMemoryVerificationAuditSink();
    await verifyMember(baseInput({ auditSink, citizenId: SyntheticCitizenIds.duplicatedMember }));

    expect(auditSink.records[0].outcome).toBe("MEMBER_DUPLICATE");
    expect(auditSink.records[0].errorCode).toBe("MEMBER_RECORD_DUPLICATE");
    expect(auditSink.records[0].memberId).toBeNull();
  });

  it("omits the correlation hash when no key is configured", async () => {
    const auditSink = new InMemoryVerificationAuditSink();
    await verifyMember(baseInput({ auditSink }));

    expect(auditSink.records[0].citizenIdCorrelationHash).toBeNull();
    expect(auditSink.records[0].maskedCitizenId).toContain("xxxxx");
  });
});

describe("statusForOutcome", () => {
  it("maps each outcome to its HTTP status", () => {
    expect(statusForOutcome("MEMBER_MATCHED")).toBe(200);
    expect(statusForOutcome("MEMBER_NOT_FOUND")).toBe(200);
    expect(statusForOutcome("MEMBER_DUPLICATE")).toBe(409);
    expect(statusForOutcome("CITIZEN_ID_INVALID")).toBe(422);
    expect(statusForOutcome("MEMBER_DATABASE_UNAVAILABLE")).toBe(503);
  });
});
