using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.ThaiCard;

public sealed class ThaiCardReadSettings
{
    /// <summary>Maximum time a single card read may take before it fails closed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Include the ATR in the result for diagnostics. Not personal data.</summary>
    public bool IncludeCardAtrForDiagnostics { get; set; }
}

/// <summary>
/// Orchestrates one Phase 13A identity read: validates preconditions, enforces a timeout and a
/// single in-flight read, delegates the card protocol to <see cref="IThaiCardDataProvider"/>, and
/// validates the returned citizen ID before it is allowed out of this method.
/// </summary>
/// <remarks>
/// <para><b>Nothing here knows the card protocol.</b> This type never issues a card command; it
/// only sequences the checks around one.</para>
/// <para><b>Nothing here logs the citizen ID.</b> The value is returned to the caller and is never
/// passed to a logger, an exception message, or an audit field.</para>
/// </remarks>
public sealed class ThaiCardIdentityReadService
{
    // One read at a time, agent-wide. A card cannot be read twice concurrently, and a double
    // submission from the UI must not turn into two reads of the same physical card.
    private readonly SemaphoreSlim _readGate = new(1, 1);

    private readonly IThaiCardDataProvider _provider;
    private readonly ISmartCardReaderService _readerService;
    private readonly ThaiCardReadSettings _settings;
    private readonly TimeProvider _timeProvider;

    public ThaiCardIdentityReadService(
        IThaiCardDataProvider provider,
        ISmartCardReaderService readerService,
        ThaiCardReadSettings? settings = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _readerService = readerService ?? throw new ArgumentNullException(nameof(readerService));
        _settings = settings ?? new ThaiCardReadSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads and validates the citizen ID. Every failure path throws an <see cref="AgentException"/>
    /// carrying a sanitized error code; no path returns partial or fabricated data.
    /// </summary>
    public async Task<ThaiIdCardIdentityResult> ReadAsync(
        string requestId,
        string? requestedReaderName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        // Fail closed before touching hardware or taking the gate: an unconfigured provider is not
        // a transient condition and must not queue behind another read.
        if (!_provider.IsConfigured)
        {
            throw new ThaiCardProtocolNotConfiguredException();
        }

        // Reject a concurrent read immediately rather than queueing it. Queueing would let a
        // double-click become two sequential reads of the same card, which is exactly the
        // duplicate the caller asked us to prevent.
        if (!await _readGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new SmartCardBusyException(requestedReaderName ?? "<default>");
        }

        try
        {
            var readerName = await ResolveReaderAsync(requestedReaderName, cancellationToken).ConfigureAwait(false);
            await EnsureCardPresentAsync(readerName, cancellationToken).ConfigureAwait(false);

            using var timeoutSource = new CancellationTokenSource(_settings.Timeout, _timeProvider);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            ThaiIdCardIdentityResult result;
            try
            {
                var context = new ThaiCardReadContext(requestId, readerName, _settings.IncludeCardAtrForDiagnostics);
                result = await _provider.ReadCitizenIdAsync(context, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Our deadline elapsed, not the caller's cancellation.
                throw new CardReadTimeoutException(readerName);
            }

            ArgumentNullException.ThrowIfNull(result);

            // Fail closed on malformed card data. The value is never repaired, and neither the
            // value nor any fragment of it appears in the exception.
            var validation = ThaiCitizenId.Validate(result.CitizenId);
            if (validation != ThaiCitizenIdValidationResult.Valid)
            {
                throw new CardDataInvalidException(validation);
            }

            return result with
            {
                RequestId = requestId,
                ReaderName = readerName,
                ProviderName = _provider.ProviderName
            };
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<string> ResolveReaderAsync(string? requestedReaderName, CancellationToken cancellationToken)
    {
        var readers = await _readerService.GetReadersAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(requestedReaderName))
        {
            // An explicitly named reader must actually exist. Silently falling back to another
            // reader could read a different person's card than the operator intended.
            var match = readers.FirstOrDefault(reader => string.Equals(reader.Name, requestedReaderName, StringComparison.Ordinal));
            if (match is null)
            {
                throw new ReaderNotFoundException(requestedReaderName);
            }

            return match.Name;
        }

        return readers.Count switch
        {
            0 => throw new ReaderNotFoundException("<default>"),
            1 => readers[0].Name,
            _ => throw new ReaderSelectionRequiredException()
        };
    }

    private async Task EnsureCardPresentAsync(string readerName, CancellationToken cancellationToken)
    {
        var status = await _readerService.GetStatusAsync(readerName, cancellationToken).ConfigureAwait(false);
        if (status.Status != SmartCardPresenceStatus.CardPresent)
        {
            throw new CardNotPresentException(readerName);
        }
    }
}
