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
    public async Task GetReadersAsync_ReturnsSingleReaderWithCardAndAtr()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x3B, 0x67, 0x00]);
        var service = CreateService(platform);

        var readers = await service.GetReadersAsync();

        var reader = Assert.Single(readers);
        Assert.Equal("Reader A", reader.Name);
        Assert.True(reader.IsCardPresent);
        Assert.Equal("3B-67-00", reader.Atr);
    }

    [Fact]
    public async Task GetReadersAsync_ReturnsMultipleReaders()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader B", SmartCardPresenceStatus.NoCard);
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x3B]);
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
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x3B, 0x67]);
        var service = CreateService(platform);

        var atr = await service.GetAtrAsync("Reader A");

        Assert.Equal([0x3B, 0x67], atr);
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
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x3B]);
        var service = CreateService(platform);

        var first = service.GetStatusAsync("Reader A");
        await Task.Delay(50);
        var second = await Assert.ThrowsAsync<SmartCardBusyException>(() => service.GetStatusAsync("Reader A"));
        platform.Release();
        await first;

        Assert.Equal("Reader A", second.ReaderName);
    }

    private static PcscSmartCardReaderService CreateService(IPcscPlatform platform) => new(platform, Options.Create(new PcscOptions()));

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
}