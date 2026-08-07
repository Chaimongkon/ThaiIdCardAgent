namespace ThaiIdCardAgent.Core;

/// <summary>
/// Outcome of an identity verification attempt, recorded for audit.
/// </summary>
public enum IdentityVerificationOutcome
{
    /// <summary>The card was read and the citizen ID passed validation.</summary>
    CardReadSucceeded,

    /// <summary>The card read failed. <see cref="IdentityVerificationAuditRecord.ErrorCode"/> says why.</summary>
    CardReadFailed,

    /// <summary>The citizen ID matched exactly one cooperative member.</summary>
    MemberMatched,

    /// <summary>The citizen ID matched no member.</summary>
    MemberNotFound,

    /// <summary>The citizen ID matched more than one member. Fails closed — never resolved by guessing.</summary>
    MemberAmbiguous,

    /// <summary>The member database could not be reached.</summary>
    MemberLookupUnavailable
}

/// <summary>
/// One audit record for one identity verification attempt.
/// </summary>
/// <remarks>
/// <para><b>This record must never carry personal data.</b> There is deliberately no field for the
/// raw citizen ID, the cardholder name, the address, the photo, an APDU trace, a JWT, or key
/// material. Correlation across records is done through <see cref="CitizenIdCorrelationHash"/> — a
/// keyed HMAC — and never through the identifier itself.</para>
/// <para><see cref="MaskedCitizenId"/> is included for human-readable operator display and shows
/// only the leading and trailing digits.</para>
/// </remarks>
/// <param name="VerificationId">Unique id for this attempt; also returned to the caller.</param>
/// <param name="TimestampUtc">When the attempt occurred.</param>
/// <param name="StaffIdentifier">Authenticated operator (JWT subject). Not personal card data.</param>
/// <param name="WorkstationIdentifier">Department/workstation the read was performed from.</param>
/// <param name="ReaderName">Reader used.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="MemberId">Cooperative member id, only when exactly one member matched.</param>
/// <param name="ErrorCode">Sanitized agent error code, only when the attempt failed.</param>
/// <param name="MaskedCitizenId">Masked form for operator display. Never the full value.</param>
/// <param name="CitizenIdCorrelationHash">Keyed HMAC, present only when correlation is required.</param>
/// <param name="ProviderName">Provider that performed the read.</param>
public sealed record IdentityVerificationAuditRecord(
    string VerificationId,
    DateTimeOffset TimestampUtc,
    string StaffIdentifier,
    string WorkstationIdentifier,
    string ReaderName,
    IdentityVerificationOutcome Outcome,
    string? MemberId = null,
    string? ErrorCode = null,
    string? MaskedCitizenId = null,
    string? CitizenIdCorrelationHash = null,
    string? ProviderName = null);

/// <summary>
/// Receives audit records. Implementations must persist only what the record carries and must not
/// enrich it with card data.
/// </summary>
public interface IIdentityVerificationAuditSink
{
    Task WriteAsync(IdentityVerificationAuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Computes the keyed correlation hash used to link audit records that concern the same person
/// without ever storing the identifier itself.
/// </summary>
/// <remarks>
/// A plain hash of a 13-digit number is trivially reversible by brute force — the whole keyspace is
/// only 10^13 and the check digit shrinks it further — so correlation uses an HMAC under a secret
/// key. Without the key the output cannot be linked back to an identifier. The key must be supplied
/// from configuration and must never be committed.
/// </remarks>
public interface ICitizenIdCorrelationHasher
{
    /// <summary>True when a correlation key is configured. When false, no hash is produced.</summary>
    bool IsEnabled { get; }

    /// <summary>Returns the keyed correlation hash, or null when correlation is not enabled.</summary>
    string? ComputeHash(string citizenId);
}
