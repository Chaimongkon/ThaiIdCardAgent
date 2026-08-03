using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

public sealed record PcscReaderState(
    string ReaderName,
    SmartCardPresenceStatus Status,
    byte[]? Atr,
    DateTimeOffset CheckedAtUtc);

public interface IPcscPlatform
{
    Task<IReadOnlyList<string>> ListReadersAsync(CancellationToken cancellationToken = default);

    Task<PcscReaderState> GetReaderStateAsync(string readerName, CancellationToken cancellationToken = default);
}

public sealed class PcscOptions
{
    public int TimeoutSeconds { get; set; } = 10;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds <= 0 ? 10 : TimeoutSeconds);
}