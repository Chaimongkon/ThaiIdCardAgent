using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.ThaiCard;
using ThaiIdCardAgent.ThaiCard.Testing;

namespace ThaiIdCardAgent.Core.Tests;

public sealed class ThaiCardIdentityReadServiceTests
{
    private const string ValidId = ThaiCitizenIdTests.SyntheticValidId;
    private const string ReaderName = "Reader A";

    [Fact]
    public async Task NotConfiguredProvider_FailsClosed()
    {
        var service = CreateService(new NotConfiguredThaiCardDataProvider());

        var exception = await Assert.ThrowsAsync<ThaiCardProtocolNotConfiguredException>(
            () => service.ReadAsync("req-1", ReaderName));

        Assert.Equal(AgentErrorCodes.ThaiCardProtocolNotConfigured, exception.Code);
    }

    [Fact]
    public async Task ConfiguredProvider_ReturnsValidatedCitizenId()
    {
        var provider = MockThaiCardDataProvider.Returning(ValidId, "official-mock");
        var service = CreateService(provider);

        var result = await service.ReadAsync("req-1", ReaderName);

        Assert.Equal(ValidId, result.CitizenId);
        Assert.Equal(ReaderName, result.ReaderName);
        Assert.Equal("req-1", result.RequestId);
        Assert.Equal("official-mock", result.ProviderName);
        Assert.True(ThaiCitizenId.IsValid(result.CitizenId));
    }

    [Theory]
    [InlineData("110170020736")]        // too short
    [InlineData("11017002073666")]      // too long
    [InlineData("110170020736X")]       // non-digit
    [InlineData("1101700207360")]       // wrong check digit
    public async Task MalformedCardData_FailsClosedAndIsNeverRepaired(string cardValue)
    {
        var service = CreateService(MockThaiCardDataProvider.Returning(cardValue));

        var exception = await Assert.ThrowsAsync<CardDataInvalidException>(
            () => service.ReadAsync("req-1", ReaderName));

        Assert.Equal(AgentErrorCodes.CardDataInvalid, exception.Code);
        // The rejected value must not travel in the error.
        Assert.DoesNotContain(cardValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardRemovedDuringRead_IsSurfacedAsItsOwnError()
    {
        var service = CreateService(MockThaiCardDataProvider.Throwing(new CardRemovedDuringReadException(ReaderName)));

        var exception = await Assert.ThrowsAsync<CardRemovedDuringReadException>(
            () => service.ReadAsync("req-1", ReaderName));

        Assert.Equal(AgentErrorCodes.CardRemovedDuringRead, exception.Code);
    }

    [Fact]
    public async Task CommunicationFailure_IsSurfacedWithoutCardContent()
    {
        var service = CreateService(MockThaiCardDataProvider.Throwing(
            new CardCommunicationException(ReaderName, new InvalidOperationException("low level detail"))));

        var exception = await Assert.ThrowsAsync<CardCommunicationException>(
            () => service.ReadAsync("req-1", ReaderName));

        Assert.Equal(AgentErrorCodes.CardCommunicationError, exception.Code);
        Assert.DoesNotContain("low level detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderUnavailable_IsSurfacedAsProviderUnavailable()
    {
        var service = CreateService(MockThaiCardDataProvider.Unavailable("official-mock"));

        var exception = await Assert.ThrowsAsync<ThaiCardProviderUnavailableException>(
            () => service.ReadAsync("req-1", ReaderName));

        Assert.Equal(AgentErrorCodes.ProviderUnavailable, exception.Code);
    }

    [Fact]
    public async Task ReadExceedingTheDeadline_TimesOut()
    {
        var service = CreateService(
            MockThaiCardDataProvider.Hanging(),
            new ThaiCardReadSettings { Timeout = TimeSpan.FromMilliseconds(150) });

        var exception = await Assert.ThrowsAsync<CardReadTimeoutException>(
            () => service.ReadAsync("req-1", ReaderName));

        Assert.Equal(AgentErrorCodes.CardReadTimeout, exception.Code);
    }

    [Fact]
    public async Task CallerCancellation_IsReportedAsCancellationNotTimeout()
    {
        var service = CreateService(
            MockThaiCardDataProvider.Hanging(),
            new ThaiCardReadSettings { Timeout = TimeSpan.FromSeconds(30) });
        using var cancellation = new CancellationTokenSource();

        var readTask = service.ReadAsync("req-1", ReaderName, cancellation.Token);
        await cancellation.CancelAsync();

        // A caller who cancels must not be told the card timed out.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task UnknownReader_IsRejectedInsteadOfFallingBackToAnother()
    {
        // Falling back to a different reader could read a different person's card.
        var service = CreateService(MockThaiCardDataProvider.Returning(ValidId));

        var exception = await Assert.ThrowsAsync<ReaderNotFoundException>(
            () => service.ReadAsync("req-1", "Reader Z"));

        Assert.Equal(AgentErrorCodes.ReaderNotFound, exception.Code);
    }

    [Fact]
    public async Task NoCardPresent_IsRejectedBeforeAnyCardCommand()
    {
        var provider = MockThaiCardDataProvider.Returning(ValidId);
        var service = CreateService(provider, readerService: new FakeReaderService(cardPresent: false));

        await Assert.ThrowsAsync<CardNotPresentException>(() => service.ReadAsync("req-1", ReaderName));
        Assert.Equal(0, provider.ReadAttempts);
    }

    [Fact]
    public async Task NoReaders_IsReportedAsReaderNotFound()
    {
        var service = CreateService(
            MockThaiCardDataProvider.Returning(ValidId),
            readerService: new FakeReaderService(readerNames: []));

        await Assert.ThrowsAsync<ReaderNotFoundException>(() => service.ReadAsync("req-1", requestedReaderName: null));
    }

    [Fact]
    public async Task MultipleReadersWithoutSelection_RequiresExplicitChoice()
    {
        var service = CreateService(
            MockThaiCardDataProvider.Returning(ValidId),
            readerService: new FakeReaderService(readerNames: ["Reader A", "Reader B"]));

        await Assert.ThrowsAsync<ReaderSelectionRequiredException>(() => service.ReadAsync("req-1", requestedReaderName: null));
    }

    [Fact]
    public async Task ConcurrentRead_IsRejectedRatherThanQueued()
    {
        // A double submission must not become two reads of the same physical card.
        var gate = new TaskCompletionSource();
        var provider = MockThaiCardDataProvider.Custom(async (context, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return new ThaiIdCardIdentityResult(context.RequestId, context.ReaderName, ValidId, DateTimeOffset.UtcNow, "mock");
        });
        var service = CreateService(provider);

        var first = service.ReadAsync("req-1", ReaderName);
        // Wait until the first read is actually inside the provider before racing the second.
        while (provider.ReadAttempts == 0)
        {
            await Task.Delay(10);
        }

        var busy = await Assert.ThrowsAsync<SmartCardBusyException>(() => service.ReadAsync("req-2", ReaderName));
        Assert.Equal(AgentErrorCodes.AgentBusy, busy.Code);

        gate.SetResult();
        var result = await first;
        Assert.Equal(ValidId, result.CitizenId);
        Assert.Equal(1, provider.ReadAttempts);
    }

    [Fact]
    public async Task SequentialReads_AreAllowedAfterTheGateIsReleased()
    {
        var provider = MockThaiCardDataProvider.Returning(ValidId);
        var service = CreateService(provider);

        await service.ReadAsync("req-1", ReaderName);
        await service.ReadAsync("req-2", ReaderName);

        Assert.Equal(2, provider.ReadAttempts);
    }

    [Fact]
    public async Task FailedRead_ReleasesTheGate()
    {
        // A provider failure must not wedge the agent into permanently reporting busy.
        var service = CreateService(MockThaiCardDataProvider.Throwing(new CardCommunicationException(ReaderName)));

        await Assert.ThrowsAsync<CardCommunicationException>(() => service.ReadAsync("req-1", ReaderName));
        await Assert.ThrowsAsync<CardCommunicationException>(() => service.ReadAsync("req-2", ReaderName));
    }

    [Fact]
    public async Task CardAtr_IsOmittedUnlessDiagnosticsAreRequested()
    {
        var withoutAtr = await CreateService(MockThaiCardDataProvider.Returning(ValidId)).ReadAsync("req-1", ReaderName);
        Assert.Null(withoutAtr.CardAtr);

        var withAtr = await CreateService(
            MockThaiCardDataProvider.Returning(ValidId),
            new ThaiCardReadSettings { IncludeCardAtrForDiagnostics = true }).ReadAsync("req-2", ReaderName);
        Assert.NotNull(withAtr.CardAtr);
    }

    private static ThaiCardIdentityReadService CreateService(
        IThaiCardDataProvider provider,
        ThaiCardReadSettings? settings = null,
        ISmartCardReaderService? readerService = null)
        => new(provider, readerService ?? new FakeReaderService(), settings);

    private sealed class FakeReaderService : ISmartCardReaderService
    {
        private readonly string[] _readerNames;
        private readonly bool _cardPresent;

        public FakeReaderService(string[]? readerNames = null, bool cardPresent = true)
        {
            _readerNames = readerNames ?? [ReaderName];
            _cardPresent = cardPresent;
        }

        public Task<IReadOnlyList<SmartCardReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SmartCardReaderInfo>>(
                _readerNames.Select(name => new SmartCardReaderInfo(name, true, _cardPresent, null, DateTimeOffset.UtcNow)).ToList());

        public Task<SmartCardStatus> GetStatusAsync(string readerName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SmartCardStatus(
                readerName,
                _cardPresent ? SmartCardPresenceStatus.CardPresent : SmartCardPresenceStatus.NoCard,
                null,
                DateTimeOffset.UtcNow));

        public Task<byte[]> GetAtrAsync(string readerName, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());
    }
}
