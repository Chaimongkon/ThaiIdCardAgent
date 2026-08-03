using Microsoft.Extensions.Options;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;

namespace ThaiIdCardAgent.Pcsc.Tests;

public sealed class PcscReaderServiceTests
{
    [Fact]
    public async Task GetReadersAsync_ReturnsNoReaders()
    {
        var service = CreateService(new InMemorySmartCardPlatform());

        var readers = await service.GetReadersAsync();

        Assert.Empty(readers);
    }

    [Fact]
    public async Task GetReadersAsync_ReaderAvailableNoCard_IsConnectedTrue()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReaderState("Reader A", PcscState.Empty);
        var service = CreateService(platform);

        var reader = Assert.Single(await service.GetReadersAsync());

        Assert.True(reader.IsConnected);
        Assert.False(reader.IsCardPresent);
        Assert.Null(reader.Atr);
    }

    [Fact]
    public async Task GetReadersAsync_ReaderAvailableCardPresent_ReturnsAtr()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReaderState("Reader A", PcscState.Present, [0x01, 0x02, 0x03]);
        var service = CreateService(platform);

        var reader = Assert.Single(await service.GetReadersAsync());

        Assert.True(reader.IsConnected);
        Assert.True(reader.IsCardPresent);
        Assert.Equal("01-02-03", reader.Atr);
    }

    [Fact]
    public async Task GetReadersAsync_StateChangedPresent_IsCardPresent()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReaderState("Reader A", PcscState.Changed | PcscState.Present, [0x11]);
        var service = CreateService(platform);

        var reader = Assert.Single(await service.GetReadersAsync());

        Assert.True(reader.IsCardPresent);
        Assert.NotEqual(PcscState.Present, PcscState.Changed | PcscState.Present);
    }

    [Fact]
    public async Task GetReadersAsync_StateChangedPresentInUse_IsCardPresent()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReaderState("Reader A", PcscState.Changed | PcscState.Present | PcscState.InUse, [0x11]);
        var service = CreateService(platform);

        var reader = Assert.Single(await service.GetReadersAsync());

        Assert.True(reader.IsConnected);
        Assert.True(reader.IsCardPresent);
    }

    [Fact]
    public async Task GetReadersAsync_StateChangedEmpty_IsNoCard()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReaderState("Reader A", PcscState.Changed | PcscState.Empty);
        var service = CreateService(platform);

        var reader = Assert.Single(await service.GetReadersAsync());

        Assert.True(reader.IsConnected);
        Assert.False(reader.IsCardPresent);
        Assert.Null(reader.Atr);
    }

    [Fact]
    public void PcscStateMapper_UsesBitwiseFlags_NotEquality()
    {
        var combined = PcscState.Changed | PcscState.Present | PcscState.InUse;

        Assert.NotEqual(PcscState.Present, combined);
        Assert.True(PcscStateMapper.IsCardPresent(combined));
        Assert.Equal(SmartCardPresenceStatus.CardPresent, PcscStateMapper.ToPresenceStatus(combined));
    }

    [Fact]
    public void TrimAtr_UsesActualLengthOnly()
    {
        var trimmed = PcscStateMapper.TrimAtr([0x01, 0x02, 0x03, 0xFF, 0xFF], 3);

        Assert.Equal([0x01, 0x02, 0x03], trimmed);
        Assert.Equal("01-02-03", AtrFormatter.ToHex(trimmed));
    }

    [Fact]
    public async Task GetReadersAsync_ReturnsMultipleReaders()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader B", SmartCardPresenceStatus.NoCard);
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x11]);
        var service = CreateService(platform);

        var readers = await service.GetReadersAsync();

        Assert.Equal(2, readers.Count);
        Assert.Contains(readers, reader => reader.Name == "Reader A");
        Assert.Contains(readers, reader => reader.Name == "Reader B");
    }

    [Fact]
    public async Task GetStatusAsync_ThrowsReaderNotFound()
    {
        var service = CreateService(new InMemorySmartCardPlatform());

        await Assert.ThrowsAsync<ReaderNotFoundException>(() => service.GetStatusAsync("Missing"));
    }

    [Fact]
    public async Task GetAtrAsync_ThrowsWhenNoCard()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);
        var service = CreateService(platform);

        await Assert.ThrowsAsync<CardNotPresentException>(() => service.GetAtrAsync("Reader A"));
    }

    [Fact]
    public async Task GetAtrAsync_ReturnsAtrBytes()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x01, 0x02]);
        var service = CreateService(platform);

        var atr = await service.GetAtrAsync("Reader A");

        Assert.Equal([0x01, 0x02], atr);
    }

    [Fact]
    public async Task GetReadersAsync_MapsSmartCardServiceFailure()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetFailure(new SmartCardServiceUnavailableException());
        var service = CreateService(platform);

        await Assert.ThrowsAsync<SmartCardServiceUnavailableException>(() => service.GetReadersAsync());
    }

    [Fact]
    public async Task GetStatusAsync_ProtectsConcurrentReaderRequests()
    {
        var platform = new SlowPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x11]);
        var service = CreateService(platform);

        var first = service.GetStatusAsync("Reader A");
        await Task.Delay(50);
        var second = await Assert.ThrowsAsync<SmartCardBusyException>(() => service.GetStatusAsync("Reader A"));
        platform.Release();
        await first;

        Assert.Equal("Reader A", second.ReaderName);
    }

    [Fact]
    public async Task Monitor_EmptyToPresent_EmitsCardInserted()
    {
        var readerService = new SequencedReaderService([
            [new SmartCardReaderInfo("Reader A", true, false, null, DateTimeOffset.UtcNow)],
            [new SmartCardReaderInfo("Reader A", true, true, "11", DateTimeOffset.UtcNow)]
        ]);
        await using var monitor = new PollingSmartCardMonitor(readerService, TimeSpan.FromMilliseconds(10));
        var events = new List<ReaderEvent>();
        monitor.ReaderEventReceived += (_, readerEvent) => events.Add(readerEvent);

        await monitor.StartAsync();
        await WaitForAsync(() => events.Any(item => item.EventType == ReaderEventType.CardInserted));
        await monitor.StopAsync();

        var inserted = Assert.Single(events, item => item.EventType == ReaderEventType.CardInserted);
        Assert.Equal("Empty", inserted.PreviousState);
        Assert.Equal("Present", inserted.NewState);
        Assert.True(inserted.IsCardPresent);
    }

    [Fact]
    public async Task Monitor_PresentToEmpty_EmitsCardRemoved()
    {
        var readerService = new SequencedReaderService([
            [new SmartCardReaderInfo("Reader A", true, true, "11", DateTimeOffset.UtcNow)],
            [new SmartCardReaderInfo("Reader A", true, false, null, DateTimeOffset.UtcNow)]
        ]);
        await using var monitor = new PollingSmartCardMonitor(readerService, TimeSpan.FromMilliseconds(10));
        var events = new List<ReaderEvent>();
        monitor.ReaderEventReceived += (_, readerEvent) => events.Add(readerEvent);

        await monitor.StartAsync();
        await WaitForAsync(() => events.Any(item => item.EventType == ReaderEventType.CardRemoved));
        await monitor.StopAsync();

        var removed = Assert.Single(events, item => item.EventType == ReaderEventType.CardRemoved);
        Assert.Equal("Present", removed.PreviousState);
        Assert.Equal("Empty", removed.NewState);
        Assert.False(removed.IsCardPresent);
    }

    [Fact]
    public async Task Monitor_Cancellation_StopsMonitor()
    {
        var readerService = new SequencedReaderService([
            [new SmartCardReaderInfo("Reader A", true, false, null, DateTimeOffset.UtcNow)]
        ]);
        await using var monitor = new PollingSmartCardMonitor(readerService, TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource();

        await monitor.StartAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await monitor.StopAsync(CancellationToken.None);

        var countAfterStop = readerService.CallCount;
        await Task.Delay(30);
        Assert.Equal(countAfterStop, readerService.CallCount);
    }

    [Fact]
    public async Task Monitor_Exception_EmitsErrorEventWithDetail()
    {
        var readerService = new ThrowingReaderService();
        await using var monitor = new PollingSmartCardMonitor(readerService, TimeSpan.FromMilliseconds(10));
        var events = new List<ReaderEvent>();
        monitor.ReaderEventReceived += (_, readerEvent) => events.Add(readerEvent);

        await monitor.StartAsync();
        await WaitForAsync(() => events.Any(item => item.EventType == ReaderEventType.Error));
        await monitor.StopAsync();

        var error = events.First(item => item.EventType == ReaderEventType.Error);
        Assert.Contains("SmartCardCommunicationException", error.TechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("0x8010002F", error.TechnicalDetail, StringComparison.Ordinal);
    }

    private static PcscSmartCardReaderService CreateService(IPcscPlatform platform) => new(platform, Options.Create(new PcscOptions()));

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class SlowPlatform : InMemorySmartCardPlatform
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<PcscReaderState> GetReaderStateAsync(string readerName, CancellationToken cancellationToken = default)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return await base.GetReaderStateAsync(readerName, cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class SequencedReaderService : ISmartCardReaderService
    {
        private readonly IReadOnlyList<IReadOnlyList<SmartCardReaderInfo>> _snapshots;
        private int _index;

        public SequencedReaderService(IReadOnlyList<IReadOnlyList<SmartCardReaderInfo>> snapshots)
        {
            _snapshots = snapshots;
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SmartCardReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var snapshot = _snapshots[Math.Min(_index, _snapshots.Count - 1)];
            _index++;
            return Task.FromResult(snapshot);
        }

        public Task<SmartCardStatus> GetStatusAsync(string readerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]> GetAtrAsync(string readerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingReaderService : ISmartCardReaderService
    {
        public Task<IReadOnlyList<SmartCardReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default)
        {
            throw new SmartCardCommunicationException("PC/SC error 0x8010002F.");
        }

        public Task<SmartCardStatus> GetStatusAsync(string readerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]> GetAtrAsync(string readerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}