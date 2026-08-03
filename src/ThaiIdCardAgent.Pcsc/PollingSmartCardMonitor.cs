using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

public sealed class PollingSmartCardMonitor : ISmartCardMonitor
{
    private readonly ISmartCardReaderService _readerService;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, SmartCardReaderInfo> _lastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stopSource;
    private Task? _monitorTask;

    public PollingSmartCardMonitor(ISmartCardReaderService readerService)
        : this(readerService, TimeSpan.FromMilliseconds(750))
    {
    }

    public PollingSmartCardMonitor(ISmartCardReaderService readerService, TimeSpan interval)
    {
        _readerService = readerService;
        _interval = interval;
    }

    public event EventHandler<ReaderEvent>? ReaderEventReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_monitorTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = Task.Run(() => MonitorAsync(_stopSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopSource is null || _monitorTask is null)
        {
            return;
        }

        await _stopSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await _monitorTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopSource?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ReaderEventReceived?.Invoke(this, new ReaderEvent(
                    string.Empty,
                    ReaderEventType.Error,
                    null,
                    DateTimeOffset.UtcNow,
                    TechnicalDetail: $"{exception.GetType().Name}: {exception.Message}"));
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var readers = await _readerService.GetReadersAsync(cancellationToken).ConfigureAwait(false);
        var current = readers.ToDictionary(reader => reader.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var reader in current.Values)
        {
            var newState = StateName(reader.IsCardPresent);
            if (!_lastSnapshot.TryGetValue(reader.Name, out var previous))
            {
                Raise(reader.Name, ReaderEventType.ReaderConnected, reader.Atr, null, newState, reader.IsCardPresent);
                if (reader.IsCardPresent)
                {
                    Raise(reader.Name, ReaderEventType.CardInserted, reader.Atr, StateName(false), newState, true);
                }

                continue;
            }

            var previousState = StateName(previous.IsCardPresent);
            if (previous.IsCardPresent != reader.IsCardPresent)
            {
                Raise(
                    reader.Name,
                    reader.IsCardPresent ? ReaderEventType.CardInserted : ReaderEventType.CardRemoved,
                    reader.Atr,
                    previousState,
                    newState,
                    reader.IsCardPresent);
            }
            else if (!string.Equals(previous.Atr, reader.Atr, StringComparison.OrdinalIgnoreCase))
            {
                Raise(reader.Name, ReaderEventType.StatusChanged, reader.Atr, previousState, newState, reader.IsCardPresent);
            }
        }

        foreach (var removed in _lastSnapshot.Keys.Except(current.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            Raise(removed, ReaderEventType.ReaderDisconnected, null, _lastSnapshot[removed].IsCardPresent ? "Present" : "Empty", null, null);
        }

        _lastSnapshot.Clear();
        foreach (var pair in current)
        {
            _lastSnapshot[pair.Key] = pair.Value;
        }
    }

    private static string StateName(bool isCardPresent) => isCardPresent ? "Present" : "Empty";

    private void Raise(string readerName, ReaderEventType eventType, string? atr, string? previousState, string? newState, bool? isCardPresent)
    {
        ReaderEventReceived?.Invoke(this, new ReaderEvent(readerName, eventType, atr, DateTimeOffset.UtcNow, previousState, newState, isCardPresent));
    }
}