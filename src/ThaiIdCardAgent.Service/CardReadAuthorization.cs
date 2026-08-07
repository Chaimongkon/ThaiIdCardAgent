using System.Security.Claims;
using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Service;

/// <summary>
/// Permissions a caller may hold. Reading identity data off a physical card is a materially more
/// sensitive operation than listing readers or checking card presence, so it requires its own
/// permission rather than riding on general authentication.
/// </summary>
public static class AgentPermissions
{
    public const string CardRead = "card.read";

    /// <summary>Authorization policy name for <see cref="CardRead"/>.</summary>
    public const string CardReadPolicy = "CardReadPolicy";

    /// <summary>Claim type carrying granted permissions, one claim per permission.</summary>
    public const string PermissionClaimType = "agent_permission";
}

/// <summary>
/// Extracts permissions from the OAuth-style <c>scope</c> claim (space-delimited) and from a
/// <c>permissions</c> claim (repeated or JSON array), normalizing both into individual claims.
/// </summary>
public static class AgentPermissionClaims
{
    public static IEnumerable<Claim> FromPrincipalClaims(IEnumerable<Claim> sourceClaims)
    {
        ArgumentNullException.ThrowIfNull(sourceClaims);
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in sourceClaims)
        {
            if (claim.Type is "scope" or "scp")
            {
                foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    permissions.Add(scope);
                }
            }
            else if (claim.Type is "permissions" or "permission")
            {
                // A JSON array claim arrives as one claim per element from JwtSecurityTokenHandler,
                // so a plain value is all that needs handling here.
                permissions.Add(claim.Value.Trim());
            }
        }

        return permissions.Select(permission => new Claim(AgentPermissions.PermissionClaimType, permission));
    }

    public static bool HasPermission(ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.HasClaim(AgentPermissions.PermissionClaimType, permission);
    }
}

/// <summary>
/// Writes identity verification audit records to the application log.
/// </summary>
/// <remarks>
/// Each field is a structured logging parameter, and <see cref="IdentityVerificationAuditRecord"/>
/// has no field capable of holding a raw citizen ID, a photo, an address, an APDU trace, or a JWT.
/// The type system is what keeps personal data out of the log here, not care at each call site.
/// </remarks>
public sealed class LoggingIdentityVerificationAuditSink : IIdentityVerificationAuditSink
{
    private readonly ILogger<LoggingIdentityVerificationAuditSink> _logger;

    public LoggingIdentityVerificationAuditSink(ILogger<LoggingIdentityVerificationAuditSink> logger)
    {
        _logger = logger;
    }

    public Task WriteAsync(IdentityVerificationAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _logger.LogInformation(
            "IdentityVerification verificationId={VerificationId} timestampUtc={TimestampUtc:O} staff={StaffIdentifier} workstation={WorkstationIdentifier} reader={ReaderName} outcome={Outcome} memberId={MemberId} errorCode={ErrorCode} maskedCitizenId={MaskedCitizenId} provider={ProviderName}",
            record.VerificationId,
            record.TimestampUtc,
            record.StaffIdentifier,
            record.WorkstationIdentifier,
            record.ReaderName,
            record.Outcome,
            record.MemberId ?? "-",
            record.ErrorCode ?? "-",
            record.MaskedCitizenId ?? "-",
            record.ProviderName ?? "-");
        return Task.CompletedTask;
    }
}
