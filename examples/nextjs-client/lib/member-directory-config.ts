/**
 * Resolves which `MemberDirectory` the production verification route runs against.
 *
 * **This resolver can never return a mock.** `MockMemberDirectory` returns fabricated member
 * records, so it is confined to the development-only page and to tests; there is deliberately no
 * environment switch that can route production traffic to it. With nothing configured the caller
 * gets `NotConfiguredMemberDirectory`, which reports `MEMBER_DB_NOT_CONFIGURED` rather than
 * reporting real people as "not found".
 */

import {
  NotConfiguredMemberDirectory,
  SqlMemberDirectory,
  validateMemberDatabaseMapping,
  type MemberDatabaseClient,
  type MemberDatabaseMapping,
  type MemberDirectory,
  type MemberDirectoryDiagnostic,
} from "./member-directory";

export type MemberDirectoryResolution = {
  directory: MemberDirectory;
  kind: "sql" | "not-configured";
  /** Non-secret note for diagnostics. Never contains a connection string or credential. */
  note: string;
};

export type ResolveMemberDirectoryOptions = {
  env?: Record<string, string | undefined>;
  /** Supplied by the host application with its real driver. */
  databaseClient?: MemberDatabaseClient;
  mapping?: MemberDatabaseMapping;
  onDiagnostic?: (event: MemberDirectoryDiagnostic) => void;
};

export function resolveMemberDirectory(options: ResolveMemberDirectoryOptions = {}): MemberDirectoryResolution {
  const env = options.env ?? process.env;

  const mapping = options.mapping ?? readMappingFromEnvironment(env);
  if (!mapping || !options.databaseClient) {
    return {
      directory: new NotConfiguredMemberDirectory(),
      kind: "not-configured",
      note: "No member database mapping and/or client configured.",
    };
  }

  return {
    directory: new SqlMemberDirectory(options.databaseClient, mapping, options.onDiagnostic),
    kind: "sql",
    note: "SQL member directory using the configured lookup statement.",
  };
}

/**
 * Reads the mapping from environment variables. Returns null when nothing is configured, and
 * throws when something is configured but invalid — a half-configured lookup must not run.
 */
export function readMappingFromEnvironment(env: Record<string, string | undefined>): MemberDatabaseMapping | null {
  const lookupSql = env.MEMBER_DB_LOOKUP_SQL;
  if (!lookupSql || lookupSql.trim().length === 0) {
    return null;
  }

  const mapping: MemberDatabaseMapping = {
    lookupSql,
    citizenIdParameter: env.MEMBER_DB_CITIZEN_ID_PARAMETER ?? "",
    columns: {
      memberId: env.MEMBER_DB_COLUMN_MEMBER_ID ?? "",
      memberNo: env.MEMBER_DB_COLUMN_MEMBER_NO ?? "",
      fullName: env.MEMBER_DB_COLUMN_FULL_NAME ?? "",
      memberType: env.MEMBER_DB_COLUMN_MEMBER_TYPE ?? null,
      memberStatus: env.MEMBER_DB_COLUMN_MEMBER_STATUS ?? null,
      photoReference: env.MEMBER_DB_COLUMN_PHOTO_REFERENCE ?? null,
    },
    queryTimeoutMs: parsePositiveInteger(env.MEMBER_DB_QUERY_TIMEOUT_MS) ?? 5_000,
  };

  return validateMemberDatabaseMapping(mapping);
}

function parsePositiveInteger(value: string | undefined): number | null {
  if (!value) return null;
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}
