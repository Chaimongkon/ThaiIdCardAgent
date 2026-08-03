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
        _readers[readerName] = new PcscReaderState(readerName, status, atr, DateTimeOffset.UtcNow);
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
}