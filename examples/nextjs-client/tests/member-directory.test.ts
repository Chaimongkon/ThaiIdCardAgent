import { describe, expect, it, vi } from "vitest";
import {
  MemberDatabaseConfigurationError,
  NotConfiguredMemberDirectory,
  SqlMemberDirectory,
  validateMemberDatabaseMapping,
  type MemberDatabaseClient,
  type MemberDatabaseMapping,
  type MemberDatabaseRow,
  type MemberDirectoryDiagnostic,
} from "@/lib/member-directory";
import { MockMemberDirectory, SyntheticCitizenIds, defaultMockMembers } from "@/lib/member-directory-mock";
import { isValidThaiCitizenId } from "@/lib/member-verification";

/**
 * The SQL below uses placeholder identifiers in angle-bracket-free form purely so the validator can
 * be exercised. No real schema is asserted anywhere in this repository.
 */
const testMapping: MemberDatabaseMapping = {
  lookupSql: "SELECT MID, MNO, MNAME, MTYPE, MSTATUS, MPHOTO FROM COOP.MEMBER WHERE CID = :citizenId",
  citizenIdParameter: ":citizenId",
  columns: {
    memberId: "MID",
    memberNo: "MNO",
    fullName: "MNAME",
    memberType: "MTYPE",
    memberStatus: "MSTATUS",
    photoReference: "MPHOTO",
  },
  queryTimeoutMs: 2_000,
};

function clientReturning(rows: MemberDatabaseRow[], capture?: (sql: string, parameters: Record<string, string>) => void): MemberDatabaseClient {
  return {
    async query(sql, parameters) {
      capture?.(sql, parameters as Record<string, string>);
      return rows;
    },
  };
}

function clientThrowing(error: unknown): MemberDatabaseClient {
  return {
    async query() {
      throw error;
    },
  };
}

const memberRow: MemberDatabaseRow = {
  MID: "M-1",
  MNO: "000001",
  MNAME: "ทดสอบ สมาชิก",
  MTYPE: "สามัญ",
  MSTATUS: "ปกติ",
  MPHOTO: "photo/000001",
};

describe("mapping validation", () => {
  it("accepts a well-formed mapping", () => {
    const validated = validateMemberDatabaseMapping(testMapping);
    expect(validated.lookupSql).toContain(":citizenId");
    expect(validated.queryTimeoutMs).toBe(2_000);
  });

  it("rejects unresolved placeholders so a half-configured lookup cannot run", () => {
    for (const field of ["lookupSql", "citizenIdParameter"] as const) {
      expect(() =>
        validateMemberDatabaseMapping({ ...testMapping, [field]: "<PLACEHOLDER>" }),
      ).toThrow(MemberDatabaseConfigurationError);
    }
    expect(() =>
      validateMemberDatabaseMapping({
        ...testMapping,
        columns: { ...testMapping.columns, memberId: "<MEMBER_ID_COLUMN>" },
      }),
    ).toThrow(MemberDatabaseConfigurationError);
  });

  it("rejects a missing required column mapping", () => {
    expect(() =>
      validateMemberDatabaseMapping({ ...testMapping, columns: { ...testMapping.columns, fullName: "" } }),
    ).toThrow(/columns.fullName/);
  });

  it("rejects statements that are not read-only SELECTs", () => {
    expect(() => validateMemberDatabaseMapping({ ...testMapping, lookupSql: "UPDATE COOP.MEMBER SET X=1 WHERE CID = :citizenId" }))
      .toThrow(/SELECT/);
    expect(() => validateMemberDatabaseMapping({ ...testMapping, lookupSql: "SELECT MID FROM COOP.MEMBER WHERE CID = :citizenId; DELETE FROM COOP.MEMBER" }))
      .toThrow(/single statement/);
    expect(() => validateMemberDatabaseMapping({ ...testMapping, lookupSql: "SELECT MID FROM COOP.MEMBER WHERE CID = :citizenId AND 1=(DELETE FROM X)" }))
      .toThrow(/delete/i);
  });

  it("requires the citizen ID to be bound, never interpolated", () => {
    expect(() =>
      validateMemberDatabaseMapping({ ...testMapping, lookupSql: "SELECT MID FROM COOP.MEMBER WHERE CID = 'abc'" }),
    ).toThrow(/citizenIdParameter/);
  });

  it("rejects a literal 13-digit value pasted into the statement", () => {
    expect(() =>
      validateMemberDatabaseMapping({
        ...testMapping,
        lookupSql: "SELECT MID FROM COOP.MEMBER WHERE CID = :citizenId OR CID = 1000000000009",
      }),
    ).toThrow(/literal 13-digit/);
  });

  it("allows a trailing semicolon", () => {
    expect(() => validateMemberDatabaseMapping({ ...testMapping, lookupSql: `${testMapping.lookupSql};` })).not.toThrow();
  });
});

describe("SqlMemberDirectory", () => {
  it("binds the citizen ID as a parameter and never interpolates it into the SQL", async () => {
    let capturedSql = "";
    let capturedParameters: Record<string, string> = {};
    const directory = new SqlMemberDirectory(
      clientReturning([memberRow], (sql, parameters) => {
        capturedSql = sql;
        capturedParameters = parameters;
      }),
      testMapping,
    );

    await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(capturedSql).not.toContain(SyntheticCitizenIds.activeMember);
    expect(capturedParameters).toEqual({ citizenId: SyntheticCitizenIds.activeMember });
  });

  it("returns Found with exactly the permitted fields", async () => {
    const directory = new SqlMemberDirectory(clientReturning([memberRow]), testMapping);

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result.status).toBe("Found");
    if (result.status !== "Found") return;
    expect(result.member).toEqual({
      memberId: "M-1",
      memberNo: "000001",
      fullName: "ทดสอบ สมาชิก",
      memberType: "สามัญ",
      memberStatus: "ปกติ",
      photoReference: "photo/000001",
    });
    // The citizen ID goes in as a key and does not come back out.
    expect(result.member).not.toHaveProperty("citizenId");
    expect(JSON.stringify(result)).not.toContain(SyntheticCitizenIds.activeMember);
  });

  it("returns NotFound for zero rows", async () => {
    const directory = new SqlMemberDirectory(clientReturning([]), testMapping);
    expect((await directory.lookupByCitizenId(SyntheticCitizenIds.unknownMember)).status).toBe("NotFound");
  });

  it("fails closed on duplicates and never inspects or returns a row", async () => {
    const secondRow = { ...memberRow, MID: "M-2", MNO: "000002" };
    const diagnostics: MemberDirectoryDiagnostic[] = [];
    const directory = new SqlMemberDirectory(
      clientReturning([memberRow, secondRow]),
      testMapping,
      (event) => diagnostics.push(event),
    );

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.duplicatedMember);

    expect(result.status).toBe("Duplicate");
    if (result.status !== "Duplicate") return;
    expect(result.matchCount).toBe(2);
    // Neither row's data may leak out of a duplicate result.
    expect(JSON.stringify(result)).not.toContain("M-1");
    expect(JSON.stringify(result)).not.toContain("M-2");
    expect(diagnostics).toContainEqual({ kind: "duplicate-match", matchCount: 2 });
  });

  it("reports an incomplete row as unavailable rather than a partial match", async () => {
    const directory = new SqlMemberDirectory(clientReturning([{ MID: "M-1", MNO: null, MNAME: "x" }]), testMapping);

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result).toEqual({ status: "DatabaseUnavailable", errorCode: "MEMBER_DB_ROW_INVALID" });
  });

  it("returns null for optional columns that are absent", async () => {
    const directory = new SqlMemberDirectory(
      clientReturning([{ MID: "M-1", MNO: "000001", MNAME: "ทดสอบ" }]),
      { ...testMapping, columns: { memberId: "MID", memberNo: "MNO", fullName: "MNAME" } },
    );

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result.status).toBe("Found");
    if (result.status !== "Found") return;
    expect(result.member.memberType).toBeNull();
    expect(result.member.memberStatus).toBeNull();
    expect(result.member.photoReference).toBeNull();
  });

  it("rejects image bytes in the photo reference column", async () => {
    // A BLOB column mapped here would otherwise be serialized onward to the browser.
    const directory = new SqlMemberDirectory(
      clientReturning([{ ...memberRow, MPHOTO: Buffer.from([0xff, 0xd8, 0xff, 0xe0]) }]),
      testMapping,
    );

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result.status).toBe("Found");
    if (result.status !== "Found") return;
    expect(result.member.photoReference).toBeNull();
  });

  it("maps a timeout to MEMBER_DB_TIMEOUT", async () => {
    const timeout = new Error("query timed out after 5000ms on COOP.MEMBER");
    timeout.name = "TimeoutError";
    const directory = new SqlMemberDirectory(clientThrowing(timeout), testMapping);

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result).toEqual({ status: "DatabaseUnavailable", errorCode: "MEMBER_DB_TIMEOUT" });
  });

  it("maps an aborted query to MEMBER_DB_TIMEOUT", async () => {
    const abort = new Error("aborted");
    abort.name = "AbortError";
    const directory = new SqlMemberDirectory(clientThrowing(abort), testMapping);

    expect((await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember))).toEqual({
      status: "DatabaseUnavailable",
      errorCode: "MEMBER_DB_TIMEOUT",
    });
  });

  it("maps a refused connection to MEMBER_DB_UNAVAILABLE", async () => {
    const refused = Object.assign(new Error("connect ECONNREFUSED 10.0.0.5:1521"), { code: "ECONNREFUSED" });
    const directory = new SqlMemberDirectory(clientThrowing(refused), testMapping);

    expect(await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember)).toEqual({
      status: "DatabaseUnavailable",
      errorCode: "MEMBER_DB_UNAVAILABLE",
    });
  });

  it("maps an arbitrary query failure to MEMBER_DB_QUERY_FAILED", async () => {
    const directory = new SqlMemberDirectory(clientThrowing(new Error("ORA-00942: table or view does not exist")), testMapping);

    expect(await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember)).toEqual({
      status: "DatabaseUnavailable",
      errorCode: "MEMBER_DB_QUERY_FAILED",
    });
  });

  it("never leaks the driver message, the SQL, or the citizen ID on failure", async () => {
    // Driver errors routinely echo the statement and its bound parameters.
    const leaky = new Error(
      `ORA-01722 while executing SELECT ... WHERE CID = '${SyntheticCitizenIds.activeMember}' user=coop_app password=hunter2`,
    );
    const diagnostics: MemberDirectoryDiagnostic[] = [];
    const directory = new SqlMemberDirectory(clientThrowing(leaky), testMapping, (event) => diagnostics.push(event));

    const result = await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);
    const serialized = JSON.stringify({ result, diagnostics });

    expect(serialized).not.toContain(SyntheticCitizenIds.activeMember);
    expect(serialized).not.toContain("hunter2");
    expect(serialized).not.toContain("coop_app");
    expect(serialized).not.toContain("SELECT");
    expect(serialized).not.toContain("ORA-01722");
  });

  it("honours the configured query timeout", async () => {
    let observedTimeout = 0;
    const directory = new SqlMemberDirectory(
      {
        async query(_sql, _parameters, options) {
          observedTimeout = options.timeoutMs;
          return [memberRow];
        },
      },
      testMapping,
    );

    await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(observedTimeout).toBe(2_000);
  });
});

describe("NotConfiguredMemberDirectory", () => {
  it("reports unavailable rather than not-found", async () => {
    // Reporting NotFound would make an unconfigured deployment look like a genuine miss.
    const result = await new NotConfiguredMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result).toEqual({ status: "DatabaseUnavailable", errorCode: "MEMBER_DB_NOT_CONFIGURED" });
  });
});

describe("mock dataset", () => {
  it("uses only checksum-valid synthetic citizen IDs", () => {
    for (const [name, value] of Object.entries(SyntheticCitizenIds)) {
      expect(isValidThaiCitizenId(value), `${name}=${value}`).toBe(true);
    }
    for (const row of defaultMockMembers()) {
      expect(isValidThaiCitizenId(row.citizenId), row.memberId).toBe(true);
    }
  });

  it("marks every mock record as obviously synthetic", () => {
    for (const row of defaultMockMembers()) {
      expect(row.memberId).toMatch(/^MOCK-/);
      expect(row.fullName).toContain("ทดสอบ");
    }
  });

  it("finds an active member", async () => {
    const result = await new MockMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.activeMember);

    expect(result.status).toBe("Found");
    if (result.status !== "Found") return;
    expect(result.member.memberNo).toBe("000001");
    expect(result.member.memberStatus).toBe("ปกติ");
  });

  it("surfaces an inactive member rather than hiding it", async () => {
    // Status is reported so staff can act on it; suppressing the record would hide the fact that
    // this person is a member at all.
    const result = await new MockMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.inactiveMember);

    expect(result.status).toBe("Found");
    if (result.status !== "Found") return;
    expect(result.member.memberStatus).toBe("ลาออก");
  });

  it("surfaces a suspended member with its status", async () => {
    const result = await new MockMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.suspendedMember);

    expect(result.status).toBe("Found");
    if (result.status !== "Found") return;
    expect(result.member.memberStatus).toBe("พักสิทธิ์");
  });

  it("returns NotFound for an unknown but valid ID", async () => {
    expect((await new MockMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.unknownMember)).status).toBe("NotFound");
  });

  it("fails closed on the duplicated synthetic ID", async () => {
    const result = await new MockMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.duplicatedMember);

    expect(result.status).toBe("Duplicate");
    if (result.status !== "Duplicate") return;
    expect(result.matchCount).toBe(2);
  });

  it("matches exactly, without trimming or normalizing", async () => {
    const directory = new MockMemberDirectory();
    expect((await directory.lookupByCitizenId(` ${SyntheticCitizenIds.activeMember}`)).status).toBe("NotFound");
    expect((await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember.slice(0, 12))).status).toBe("NotFound");
  });

  it("can simulate database unavailability", async () => {
    const directory = new MockMemberDirectory(undefined, { failWith: "MEMBER_DB_UNAVAILABLE" });

    expect(await directory.lookupByCitizenId(SyntheticCitizenIds.activeMember)).toEqual({
      status: "DatabaseUnavailable",
      errorCode: "MEMBER_DB_UNAVAILABLE",
    });
  });

  it("never returns a citizen ID in a Found result", async () => {
    const spy = vi.fn();
    const result = await new MockMemberDirectory().lookupByCitizenId(SyntheticCitizenIds.activeMember);
    spy(result);

    expect(JSON.stringify(result)).not.toContain(SyntheticCitizenIds.activeMember);
  });
});
