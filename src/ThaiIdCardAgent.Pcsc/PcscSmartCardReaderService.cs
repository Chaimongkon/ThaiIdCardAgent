using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

public sealed class PcscSmartCardReaderService : ISmartCardReaderService
{
    private readonly IPcscPlatform _platform;
    private readonly PcscOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _readerLocks = new(StringComparer.OrdinalIgnoreCase);

    public PcscSmartCardReaderService(IPcscPlatform platform, IOptions<PcscOptions> options)
    {
        _platform = platform;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SmartCardReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeout(cancellationToken);
        var readers = await _platform.ListReadersAsync(timeout.Token).ConfigureAwait(false);
        var result = new List<SmartCardReaderInfo>(readers.Count);
        foreach (var reader in readers)
        {
            try
            {
                var state = await _platform.GetReaderStateAsync(reader, timeout.Token).ConfigureAwait(false);
                result.Add(new SmartCardReaderInfo(reader, true, state.Status == SmartCardPresenceStatus.CardPresent, ToAtrHex(state.Atr), state.CheckedAtUtc));
            }
            catch (AgentException)
            {
                result.Add(new SmartCardReaderInfo(reader, false, false, null, DateTimeOffset.UtcNow));
            }
        }

        return result;
    }

    public async Task<SmartCardStatus> GetStatusAsync(string readerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerName);
        using var timeout = CreateTimeout(cancellationToken);
        var gate = _readerLocks.GetOrAdd(readerName, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, timeout.Token).ConfigureAwait(false))
        {
            throw new SmartCardBusyException(readerName);
        }

        try
        {
            var state = await _platform.GetReaderStateAsync(readerName, timeout.Token).ConfigureAwait(false);
            return new SmartCardStatus(readerName, state.Status, ToAtrHex(state.Atr), state.CheckedAtUtc);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<byte[]> GetAtrAsync(string readerName, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(readerName, cancellationToken).ConfigureAwait(false);
        if (status.Status != SmartCardPresenceStatus.CardPresent)
        {
            throw new CardNotPresentException(readerName);
        }

        var state = await _platform.GetReaderStateAsync(readerName, cancellationToken).ConfigureAwait(false);
        return state.Atr is { Length: > 0 } ? state.Atr : throw new CardNotPresentException(readerName);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);
        return source;
    }

    private static string? ToAtrHex(byte[]? atr) => atr is { Length: > 0 } ? AtrFormatter.ToHex(atr) : null;
}