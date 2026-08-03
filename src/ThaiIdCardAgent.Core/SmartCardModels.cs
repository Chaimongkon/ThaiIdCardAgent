namespace ThaiIdCardAgent.Core;

public sealed record SmartCardReaderInfo(
    string Name,
    bool IsConnected,
    bool IsCardPresent,
    string? Atr,
    DateTimeOffset CheckedAtUtc);

public enum SmartCardPresenceStatus
{
    Unknown,
    ReaderUnavailable,
    NoCard,
    CardPresent,
    CardMute,
    CardUnpowered
}

public sealed record SmartCardStatus(
    string ReaderName,
    SmartCardPresenceStatus Status,
    string? Atr,
    DateTimeOffset CheckedAtUtc);

public sealed record ReaderEvent(
    string ReaderName,
    ReaderEventType EventType,
    string? Atr,
    DateTimeOffset OccurredAtUtc,
    string? PreviousState = null,
    string? NewState = null,
    bool? IsCardPresent = null,
    string? TechnicalDetail = null);

public enum ReaderEventType
{
    ReaderConnected,
    ReaderDisconnected,
    CardInserted,
    CardRemoved,
    StatusChanged,
    Error
}

public sealed record OperationResult<T>(bool Success, T? Data, AgentError? Error)
{
    public static OperationResult<T> Ok(T data) => new(true, data, null);

    public static OperationResult<T> Fail(AgentError error) => new(false, default, error);
}

public sealed record AgentError(string Code, string Message, string? TechnicalDetail = null);

public interface ISmartCardReaderService
{
    Task<IReadOnlyList<SmartCardReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default);

    Task<SmartCardStatus> GetStatusAsync(string readerName, CancellationToken cancellationToken = default);

    Task<byte[]> GetAtrAsync(string readerName, CancellationToken cancellationToken = default);
}

public interface ISmartCardMonitor : IAsyncDisposable
{
    event EventHandler<ReaderEvent>? ReaderEventReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}