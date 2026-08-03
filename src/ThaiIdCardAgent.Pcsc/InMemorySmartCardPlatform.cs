using System.Collections.Concurrent;
using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

public class InMemorySmartCardPlatform : IPcscPlatform
{
    private readonly ConcurrentDictionary<string, PcscReaderState> _readers = new(StringComparer.OrdinalIgnoreCase);
    private Exception? _failure;

    public void SetFailure(Exception? failure) => _failure = failure;

    public void SetReader(string readerName, SmartCardPresenceStatus status, byte[]? atr = null)
    {
        SetReaderState(readerName, ToEventState(status), atr);
    }

    public void SetReaderState(string readerName, PcscState eventState, byte[]? atr = null, int? atrLength = null)
    {
        var trimmedAtr = PcscStateMapper.TrimAtr(atr, atrLength ?? atr?.Length ?? 0);
        _readers[readerName] = new PcscReaderState(
            readerName,
            PcscStateMapper.ToPresenceStatus(eventState),
            trimmedAtr.Length > 0 ? trimmedAtr : null,
            DateTimeOffset.UtcNow,
            PcscState.Unaware,
            eventState,
            trimmedAtr.Length);
    }

    public void RemoveReader(string readerName) => _readers.TryRemove(readerName, out _);

    public virtual Task<IReadOnlyList<string>> ListReadersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        return Task.FromResult<IReadOnlyList<string>>(_readers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public virtual Task<PcscReaderState> GetReaderStateAsync(string readerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        if (!_readers.TryGetValue(readerName, out var state))
        {
            throw new ReaderNotFoundException(readerName);
        }

        return Task.FromResult(state with { CheckedAtUtc = DateTimeOffset.UtcNow });
    }

    private void ThrowIfConfigured()
    {
        if (_failure is not null)
        {
            throw _failure;
        }
    }

    private static PcscState ToEventState(SmartCardPresenceStatus status) => status switch
    {
        SmartCardPresenceStatus.ReaderUnavailable => PcscState.Unavailable,
        SmartCardPresenceStatus.NoCard => PcscState.Empty,
        SmartCardPresenceStatus.CardPresent => PcscState.Present,
        SmartCardPresenceStatus.CardMute => PcscState.Present | PcscState.Mute,
        SmartCardPresenceStatus.CardUnpowered => PcscState.Present | PcscState.Unpowered,
        _ => PcscState.Unaware
    };
}