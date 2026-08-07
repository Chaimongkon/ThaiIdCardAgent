/**
 * Cooperative member directory: the seam between identity verification and the cooperative's
 * member database.
 *
 * ## Why the SQL is configuration, not code
 *
 * No table name, column name, package name, or connection string is defined in this repository,
 * because none has been supplied. Rather than invent a schema that would have to be rewritten, the
 * lookup statement and the column mapping are supplied by the cooperative's DBA through
 * configuration, and this module refuses to run while any value is still an unresolved
 * `<PLACEHOLDER>`.
 *
 * ## Why the driver is injected
 *
 * The database engine has not been confirmed either, so this module does not depend on an Oracle,
 * SQL Server, or PostgreSQL driver. The host application implements `MemberDatabaseClient` with
 * whatever driver it actually uses; this module only knows how to ask a question and interpret the
 * answer.
 */

/** Outcome of a member lookup. There is no "maybe" — every path is one of these four. */
export type MemberLookupResult =
  | { status: "Found"; member: MemberRecord }
  | { status: "NotFound" }
  /** More than one member row matched. Fails closed; the rows are never inspected further. */
  | { status: "Duplicate"; matchCount: number }
  | { status: "DatabaseUnavailable"; errorCode: MemberDatabaseErrorCode };

export type MemberDatabaseErrorCode =
  | "MEMBER_DB_NOT_CONFIGURED"
  | "MEMBER_DB_TIMEOUT"
  | "MEMBER_DB_UNAVAILABLE"
  | "MEMBER_DB_QUERY_FAILED"
  | "MEMBER_DB_ROW_INVALID";

/**
 * What the cooperative database is permitted to return about a member.
 *
 * There is deliberately **no `citizenId` field**. The citizen ID goes in as a lookup key and does
 * not come back out: once the match is made, the system holds member identity, not the national
 * identifier.
 *
 * `photoReference` is an identifier for a photo the cooperative system already holds (a URL, key,
 * or id). It is never image bytes — the agent does not read the card photo, and photo bytes must
 * never flow through this path.
 */
export type MemberRecord = {
  memberId: string;
  memberNo: string;
  fullName: string;
  memberType: string | null;
  memberStatus: string | null;
  photoReference: string | null;
};

export interface MemberDirectory {
  /** Looks up a member by an exact 13-digit citizen ID match. */
  lookupByCitizenId(citizenId: string): Promise<MemberLookupResult>;
}

// ---------------------------------------------------------------------------------------------
// Database client seam
// ---------------------------------------------------------------------------------------------

export type MemberDatabaseRow = Record<string, unknown>;

/**
 * Implemented by the host application with its real database driver.
 *
 * The citizen ID is passed as a **bind parameter**, never interpolated into the statement. An
 * implementation must not log the parameter values: they carry the citizen ID.
 */
export interface MemberDatabaseClient {
  query(
    sql: string,
    parameters: Readonly<Record<string, string>>,
    options: { timeoutMs: number; signal?: AbortSignal },
  ): Promise<MemberDatabaseRow[]>;
}

// ---------------------------------------------------------------------------------------------
// Schema mapping
// ---------------------------------------------------------------------------------------------

export type MemberColumnMapping = {
  memberId: string;
  memberNo: string;
  fullName: string;
  memberType?: string | null;
  memberStatus?: string | null;
  photoReference?: string | null;
};

export type MemberDatabaseMapping = {
  /**
   * The lookup statement, written by the cooperative's DBA. Must select at most the columns named
   * in `columns` and must match the citizen ID with an exact equality on a bind parameter.
   */
  lookupSql: string;
  /** Bind parameter name as it appears in `lookupSql` (e.g. `:citizenId`, `@citizenId`, `$1`). */
  citizenIdParameter: string;
  columns: MemberColumnMapping;
  queryTimeoutMs?: number;
};

const placeholderPattern = /^\s*<.*>\s*$/;
const requiredColumnKeys = ["memberId", "memberNo", "fullName"] as const;

/** Statements that must never appear in a lookup: this path reads, it never writes. */
const forbiddenSqlKeywords = [
  "insert",
  "update",
  "delete",
  "merge",
  "drop",
  "truncate",
  "alter",
  "create",
  "grant",
  "revoke",
  "execute",
  "exec",
  "call",
];

export class MemberDatabaseConfigurationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "MemberDatabaseConfigurationError";
  }
}

/**
 * Validates an operator-supplied mapping and fails closed on anything suspicious.
 *
 * This is defense in depth, not a SQL parser: it cannot prove an arbitrary statement is safe. Its
 * job is to catch the mistakes that actually happen — an unfilled placeholder, a statement that
 * writes, a second statement smuggled in after a semicolon, or a citizen ID pasted into the SQL
 * instead of bound as a parameter.
 */
export function validateMemberDatabaseMapping(mapping: MemberDatabaseMapping): MemberDatabaseMapping {
  if (!mapping || typeof mapping !== "object") {
    throw new MemberDatabaseConfigurationError("Member database mapping is missing.");
  }

  const sql = (mapping.lookupSql ?? "").trim();
  const parameter = (mapping.citizenIdParameter ?? "").trim();

  if (sql.length === 0 || placeholderPattern.test(sql)) {
    throw new MemberDatabaseConfigurationError(
      "lookupSql is not configured. Supply the lookup statement confirmed by the cooperative DBA.",
    );
  }
  if (parameter.length === 0 || placeholderPattern.test(parameter)) {
    throw new MemberDatabaseConfigurationError("citizenIdParameter is not configured.");
  }

  for (const key of requiredColumnKeys) {
    const value = (mapping.columns?.[key] ?? "").trim();
    if (value.length === 0 || placeholderPattern.test(value)) {
      throw new MemberDatabaseConfigurationError(`columns.${key} is not configured.`);
    }
  }

  const normalized = sql.toLowerCase();
  if (!normalized.startsWith("select") && !normalized.startsWith("with")) {
    throw new MemberDatabaseConfigurationError("lookupSql must be a SELECT statement.");
  }

  // A trailing semicolon is fine; one in the middle means a second statement.
  const withoutTrailingSemicolon = sql.replace(/;\s*$/, "");
  if (withoutTrailingSemicolon.includes(";")) {
    throw new MemberDatabaseConfigurationError("lookupSql must be a single statement.");
  }

  for (const keyword of forbiddenSqlKeywords) {
    if (new RegExp(`\\b${keyword}\\b`, "i").test(withoutTrailingSemicolon)) {
      throw new MemberDatabaseConfigurationError(
        `lookupSql must not contain '${keyword}'. Member lookup is read-only.`,
      );
    }
  }

  if (!sql.includes(parameter)) {
    throw new MemberDatabaseConfigurationError(
      "lookupSql does not reference citizenIdParameter. The citizen ID must be bound as a parameter, never interpolated.",
    );
  }

  // A literal 13-digit run in the statement means someone pasted an identifier into the SQL.
  if (/\b\d{13}\b/.test(withoutTrailingSemicolon)) {
    throw new MemberDatabaseConfigurationError(
      "lookupSql contains a literal 13-digit value. The citizen ID must be bound as a parameter.",
    );
  }

  return {
    ...mapping,
    lookupSql: sql,
    citizenIdParameter: parameter,
    queryTimeoutMs: mapping.queryTimeoutMs ?? 5_000,
  };
}

// ---------------------------------------------------------------------------------------------
// SQL-backed directory
// ---------------------------------------------------------------------------------------------

/**
 * Looks a member up through an operator-supplied statement and an injected database client.
 *
 * Every failure is mapped to a sanitized `MemberDatabaseErrorCode`. Driver messages, SQL text,
 * connection strings, and parameter values never escape this class, because any of them could
 * quote the citizen ID or a credential.
 */
export class SqlMemberDirectory implements MemberDirectory {
  private readonly mapping: MemberDatabaseMapping;

  constructor(
    private readonly client: MemberDatabaseClient,
    mapping: MemberDatabaseMapping,
    private readonly onDiagnostic?: (event: MemberDirectoryDiagnostic) => void,
  ) {
    this.mapping = validateMemberDatabaseMapping(mapping);
  }

  async lookupByCitizenId(citizenId: string): Promise<MemberLookupResult> {
    const timeoutMs = this.mapping.queryTimeoutMs ?? 5_000;
    let rows: MemberDatabaseRow[];

    try {
      rows = await this.client.query(
        this.mapping.lookupSql,
        { [this.stripParameterSigil(this.mapping.citizenIdParameter)]: citizenId },
        { timeoutMs },
      );
    } catch (error) {
      const errorCode = classifyDatabaseError(error);
      // The diagnostic carries a code and a name only. The message is withheld because driver
      // errors routinely echo the statement and its bound parameters.
      this.onDiagnostic?.({ kind: "lookup-failed", errorCode, errorName: safeErrorName(error) });
      return { status: "DatabaseUnavailable", errorCode };
    }

    if (!Array.isArray(rows) || rows.length === 0) {
      return { status: "NotFound" };
    }

    // Fail closed on duplicates. The rows are deliberately not inspected: selecting the first one
    // could attach the wrong person to a transaction, and there is no basis for preferring either.
    if (rows.length > 1) {
      this.onDiagnostic?.({ kind: "duplicate-match", matchCount: rows.length });
      return { status: "Duplicate", matchCount: rows.length };
    }

    const member = this.mapRow(rows[0]);
    if (!member) {
      this.onDiagnostic?.({ kind: "row-invalid" });
      return { status: "DatabaseUnavailable", errorCode: "MEMBER_DB_ROW_INVALID" };
    }

    return { status: "Found", member };
  }

  /** Drivers vary on whether the bind name includes its sigil; the client receives it bare. */
  private stripParameterSigil(parameter: string): string {
    return parameter.replace(/^[:@$]/, "");
  }

  private mapRow(row: MemberDatabaseRow): MemberRecord | null {
    const columns = this.mapping.columns;
    const memberId = readString(row, columns.memberId);
    const memberNo = readString(row, columns.memberNo);
    const fullName = readString(row, columns.fullName);

    // The required identity fields must be present; a partial member record is not a match.
    if (memberId === null || memberNo === null || fullName === null) {
      return null;
    }

    return {
      memberId,
      memberNo,
      fullName,
      memberType: columns.memberType ? readString(row, columns.memberType) : null,
      memberStatus: columns.memberStatus ? readString(row, columns.memberStatus) : null,
      photoReference: columns.photoReference ? readPhotoReference(row, columns.photoReference) : null,
    };
  }
}

export type MemberDirectoryDiagnostic =
  | { kind: "lookup-failed"; errorCode: MemberDatabaseErrorCode; errorName: string }
  | { kind: "duplicate-match"; matchCount: number }
  | { kind: "row-invalid" };

/**
 * A directory that is not configured. Returns `DatabaseUnavailable` rather than pretending a member
 * does not exist, so an unconfigured deployment cannot be mistaken for a genuine NotFound.
 */
export class NotConfiguredMemberDirectory implements MemberDirectory {
  // The citizen ID is accepted and immediately discarded: it is never inspected, stored, or logged.
  async lookupByCitizenId(_citizenId: string): Promise<MemberLookupResult> {
    void _citizenId;
    return { status: "DatabaseUnavailable", errorCode: "MEMBER_DB_NOT_CONFIGURED" };
  }
}

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------

function readString(row: MemberDatabaseRow, column: string): string | null {
  const value = row[column];
  if (value === null || value === undefined) return null;
  if (typeof value === "string") return value.trim().length > 0 ? value.trim() : null;
  if (typeof value === "number" || typeof value === "bigint") return String(value);
  return null;
}

/**
 * Reads a photo *reference*. Anything that looks like image bytes is rejected: photo bytes must
 * never traverse this path, and a driver returning a BLOB here would otherwise be serialized
 * onward.
 */
function readPhotoReference(row: MemberDatabaseRow, column: string): string | null {
  const value = row[column];
  if (value === null || value === undefined) return null;
  if (typeof value === "string") {
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  }
  if (typeof value === "number" || typeof value === "bigint") return String(value);
  // Buffer, Uint8Array, ArrayBuffer, or anything else: not a reference.
  return null;
}

function classifyDatabaseError(error: unknown): MemberDatabaseErrorCode {
  const name = safeErrorName(error).toLowerCase();
  if (name.includes("abort") || name.includes("timeout")) return "MEMBER_DB_TIMEOUT";

  const code = typeof error === "object" && error !== null && "code" in error ? String((error as { code: unknown }).code) : "";
  const normalizedCode = code.toLowerCase();
  if (normalizedCode.includes("etimedout") || normalizedCode.includes("timeout")) return "MEMBER_DB_TIMEOUT";
  if (
    normalizedCode.includes("econnrefused") ||
    normalizedCode.includes("ehostunreach") ||
    normalizedCode.includes("enotfound") ||
    normalizedCode.includes("econnreset")
  ) {
    return "MEMBER_DB_UNAVAILABLE";
  }

  return "MEMBER_DB_QUERY_FAILED";
}

/** Returns the error's type name only — never its message, which may quote SQL or parameters. */
function safeErrorName(error: unknown): string {
  if (error instanceof Error) return error.name || "Error";
  if (typeof error === "object" && error !== null && "name" in error) {
    const name = (error as { name: unknown }).name;
    if (typeof name === "string") return name;
  }
  return "UnknownError";
}
