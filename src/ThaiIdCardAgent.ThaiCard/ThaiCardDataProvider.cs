using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.ThaiCard;

/// <summary>
/// The minimal result of a Phase 13A identity read: the citizen ID and nothing else about the
/// person. Photo, address, name, birth date, religion, and every other cardholder attribute are
/// deliberately absent from this contract so they cannot be read, returned, or logged by accident.
/// </summary>
/// <param name="RequestId">Correlates the read with the API request and the audit record.</param>
/// <param name="ReaderName">Reader the card was read from.</param>
/// <param name="CitizenId">13 decimal digits, checksum-validated. Never logged.</param>
/// <param name="ReadAtUtc">When the read completed.</param>
/// <param name="ProviderName">Which provider produced the value, for traceability.</param>
/// <param name="CardAtr">Optional, diagnostics only. Not personal data; omitted unless requested.</param>
public sealed record ThaiIdCardIdentityResult(
    string RequestId,
    string ReaderName,
    string CitizenId,
    DateTimeOffset ReadAtUtc,
    string ProviderName,
    string? CardAtr = null);

/// <summary>Inputs for a single card read.</summary>
public sealed record ThaiCardReadContext(
    string RequestId,
    string ReaderName,
    bool IncludeCardAtrForDiagnostics = false);

/// <summary>
/// Reads identity data from a physical Thai national ID card.
/// </summary>
/// <remarks>
/// This is the only seam through which card commands may be issued. Implementations own the card
/// protocol entirely: no APDU constant, command sequence, or data-decoding rule may appear in an
/// endpoint, in <c>Program.cs</c>, in the PC/SC reader enumeration layer, in the SSE monitor, or in
/// the web client.
/// <para>
/// A real implementation may only be written against official technical material issued by
/// Thailand's Department of Provincial Administration or supplied under an authorized integration
/// agreement. Command sets recovered from blogs, unofficial repositories, forum posts, or
/// undocumented third-party libraries must not be used.
/// </para>
/// </remarks>
public interface IThaiCardDataProvider
{
    /// <summary>Stable identifier recorded in results and audit records.</summary>
    string ProviderName { get; }

    /// <summary>
    /// False when the provider cannot perform a real read. The endpoint fails closed on this
    /// rather than returning placeholder data.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Reads the 13-digit citizen ID from the card currently in <paramref name="context"/>'s reader.
    /// </summary>
    /// <exception cref="ThaiCardProtocolNotConfiguredException">No authorized provider is configured.</exception>
    /// <exception cref="CardReadTimeoutException">The read exceeded its deadline.</exception>
    /// <exception cref="CardRemovedDuringReadException">The card was withdrawn mid-read.</exception>
    /// <exception cref="CardCommunicationException">Card communication failed.</exception>
    /// <exception cref="CardDataInvalidException">The card returned data that failed validation.</exception>
    /// <exception cref="ThaiCardProviderUnavailableException">The provider exists but cannot serve the request.</exception>
    Task<ThaiIdCardIdentityResult> ReadCitizenIdAsync(ThaiCardReadContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default provider. Always fails closed with <c>THAI_CARD_PROTOCOL_NOT_CONFIGURED</c>.
/// </summary>
/// <remarks>
/// This is what ships until an authorized provider has been integrated and validated against real
/// hardware. It contains no card protocol knowledge of any kind, which is the point: an absent
/// provider must be an explicit, visible failure rather than an empty or fabricated result.
/// </remarks>
public sealed class NotConfiguredThaiCardDataProvider : IThaiCardDataProvider
{
    public string ProviderName => "not-configured";

    public bool IsConfigured => false;

    public Task<ThaiIdCardIdentityResult> ReadCitizenIdAsync(ThaiCardReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        throw new ThaiCardProtocolNotConfiguredException();
    }
}
