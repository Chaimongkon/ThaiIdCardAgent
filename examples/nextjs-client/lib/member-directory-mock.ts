/**
 * Development- and test-only member directory.
 *
 * **Never register this in production.** `resolveMemberDirectory` refuses to return it outside
 * development, and a test asserts that.
 *
 * Every citizen ID below is a **synthetic, checksum-valid** value chosen so it is obviously not a
 * real identifier (repeated digits, sequential runs, all-zero bodies). Real member data must never
 * appear here, in a fixture, or in a commit.
 */

import type {
  MemberDatabaseErrorCode,
  MemberDirectory,
  MemberLookupResult,
  MemberRecord,
} from "./member-directory";

/**
 * Synthetic citizen IDs used by the mock dataset. Each has a valid Thai check digit so it exercises
 * the real validation path, but the body is patterned so it cannot be mistaken for a real ID.
 */
export const SyntheticCitizenIds = {
  /** Active ordinary member. */
  activeMember: "1000000000009",
  /** Active associate member with a photo reference. */
  activeMemberWithPhoto: "2123456789012",
  /** Inactive / resigned member — matches, but the status must be surfaced, not hidden. */
  inactiveMember: "5999000111229",
  /** Suspended member. */
  suspendedMember: "1111111111119",
  /** Deliberately duplicated across two rows, to exercise the fail-closed duplicate path. */
  duplicatedMember: "3100600445716",
  /** Valid checksum, present in no member record. */
  unknownMember: "1101700207366",
} as const;

type MockMemberRow = MemberRecord & { citizenId: string };

/** Obviously synthetic member records. No real name, member number, or photo appears here. */
export function defaultMockMembers(): MockMemberRow[] {
  return [
    {
      citizenId: SyntheticCitizenIds.activeMember,
      memberId: "MOCK-M-0001",
      memberNo: "000001",
      fullName: "ทดสอบ สมาชิกสามัญ",
      memberType: "สามัญ",
      memberStatus: "ปกติ",
      photoReference: null,
    },
    {
      citizenId: SyntheticCitizenIds.activeMemberWithPhoto,
      memberId: "MOCK-M-0002",
      memberNo: "000002",
      fullName: "ทดสอบ สมาชิกสมทบ",
      memberType: "สมทบ",
      memberStatus: "ปกติ",
      photoReference: "mock-photo/000002",
    },
    {
      citizenId: SyntheticCitizenIds.inactiveMember,
      memberId: "MOCK-M-0003",
      memberNo: "000003",
      fullName: "ทดสอบ สมาชิกลาออก",
      memberType: "สามัญ",
      memberStatus: "ลาออก",
      photoReference: null,
    },
    {
      citizenId: SyntheticCitizenIds.suspendedMember,
      memberId: "MOCK-M-0004",
      memberNo: "000004",
      fullName: "ทดสอบ สมาชิกพักสิทธิ์",
      memberType: "สามัญ",
      memberStatus: "พักสิทธิ์",
      photoReference: null,
    },
    // Two rows sharing one citizen ID: a data-integrity fault the system must fail closed on.
    {
      citizenId: SyntheticCitizenIds.duplicatedMember,
      memberId: "MOCK-M-0005",
      memberNo: "000005",
      fullName: "ทดสอบ สมาชิกซ้ำ ก",
      memberType: "สามัญ",
      memberStatus: "ปกติ",
      photoReference: null,
    },
    {
      citizenId: SyntheticCitizenIds.duplicatedMember,
      memberId: "MOCK-M-0006",
      memberNo: "000006",
      fullName: "ทดสอบ สมาชิกซ้ำ ข",
      memberType: "สามัญ",
      memberStatus: "ปกติ",
      photoReference: null,
    },
  ];
}

export type MockMemberDirectoryOptions = {
  /** Force every lookup to report the database as unavailable. */
  failWith?: MemberDatabaseErrorCode;
  /** Artificial delay, for exercising caller-side timeouts. */
  delayMs?: number;
};

export class MockMemberDirectory implements MemberDirectory {
  private readonly rows: MockMemberRow[];

  constructor(
    rows: MockMemberRow[] = defaultMockMembers(),
    private readonly options: MockMemberDirectoryOptions = {},
  ) {
    this.rows = rows;
  }

  async lookupByCitizenId(citizenId: string): Promise<MemberLookupResult> {
    if (this.options.delayMs) {
      await new Promise((resolve) => setTimeout(resolve, this.options.delayMs));
    }
    if (this.options.failWith) {
      return { status: "DatabaseUnavailable", errorCode: this.options.failWith };
    }

    // Exact match only. No trimming, no normalization, no partial match.
    const matches = this.rows.filter((row) => row.citizenId === citizenId);
    if (matches.length === 0) return { status: "NotFound" };
    if (matches.length > 1) return { status: "Duplicate", matchCount: matches.length };

    const { citizenId: _omitted, ...member } = matches[0];
    void _omitted;
    return { status: "Found", member };
  }
}
