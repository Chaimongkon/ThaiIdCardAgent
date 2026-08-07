using Microsoft.Extensions.Options;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;
using ThaiIdCardAgent.ThaiCard;

var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "diagnostics";
var readerName = GetOption(args, "--reader");
var platform = new WinSCardPlatform();
var service = new PcscSmartCardReaderService(platform, Options.Create(new PcscOptions()));
var thaiCardProvider = new NotConfiguredThaiCardDataProvider();

try
{
    return command switch
    {
        "readers" => await ShowReadersAsync(service, cancellation.Token).ConfigureAwait(false),
        "status" => await ShowStatusAsync(service, readerName, cancellation.Token).ConfigureAwait(false),
        "atr" => await ShowAtrAsync(service, readerName, cancellation.Token).ConfigureAwait(false),
        "monitor" => await MonitorAsync(service, cancellation.Token).ConfigureAwait(false),
        "diagnostics" => await ShowDiagnosticsAsync(service, platform, cancellation.Token).ConfigureAwait(false),
        "read" => await ReadThaiCardAsync(thaiCardProvider, service, readerName, cancellation.Token).ConfigureAwait(false),
        _ => ShowUsage()
    };
}
catch (ReaderNotFoundException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (CardNotPresentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 3;
}
catch (SmartCardServiceUnavailableException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 4;
}
catch (ThaiCardProtocolNotConfiguredException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 5;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (TimeoutException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 7;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> ShowReadersAsync(ISmartCardReaderService service, CancellationToken cancellationToken)
{
    var readers = await service.GetReadersAsync(cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Reader count: {readers.Count}");
    foreach (var reader in readers)
    {
        Console.WriteLine($"- {reader.Name}");
        Console.WriteLine($"  Connected: {reader.IsConnected}");
        Console.WriteLine($"  Card present: {reader.IsCardPresent}");
        Console.WriteLine($"  ATR: {reader.Atr ?? "-"}");
    }

    return readers.Count == 0 ? 2 : 0;
}

static async Task<int> ShowStatusAsync(ISmartCardReaderService service, string? readerName, CancellationToken cancellationToken)
{
    Console.WriteLine("Windows Smart Card Service: checked through PC/SC context");
    if (string.IsNullOrWhiteSpace(readerName))
    {
        var readers = await service.GetReadersAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Reader count: {readers.Count}");
        foreach (var reader in readers)
        {
            Console.WriteLine($"- {reader.Name}: connected={reader.IsConnected}, cardPresent={reader.IsCardPresent}, atr={reader.Atr ?? "-"}");
        }

        return readers.Count == 0 ? 2 : 0;
    }

    var status = await service.GetStatusAsync(readerName, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Reader: {status.ReaderName}");
    Console.WriteLine($"Status: {status.Status}");
    Console.WriteLine($"ATR: {status.Atr ?? "-"}");
    return status.Status == SmartCardPresenceStatus.NoCard ? 3 : 0;
}

static async Task<int> ShowAtrAsync(ISmartCardReaderService service, string? readerName, CancellationToken cancellationToken)
{
    var resolvedReader = await ResolveReaderAsync(service, readerName, cancellationToken).ConfigureAwait(false);
    var atr = await service.GetAtrAsync(resolvedReader, cancellationToken).ConfigureAwait(false);
    Console.WriteLine(AtrFormatter.ToHex(atr));
    return 0;
}

static async Task<int> MonitorAsync(ISmartCardReaderService service, CancellationToken cancellationToken)
{
    await using var monitor = new PollingSmartCardMonitor(service);
    monitor.ReaderEventReceived += (_, readerEvent) =>
    {
        if (readerEvent.EventType == ReaderEventType.Error)
        {
            Console.WriteLine($"{readerEvent.OccurredAtUtc:O} MonitorException {readerEvent.TechnicalDetail}");
            return;
        }

        Console.WriteLine(
            $"{readerEvent.OccurredAtUtc:O} {readerEvent.EventType} ReaderName=\"{readerEvent.ReaderName}\" PreviousState={readerEvent.PreviousState ?? "-"} NewState={readerEvent.NewState ?? "-"} CardPresent={readerEvent.IsCardPresent?.ToString() ?? "-"} ATR={readerEvent.Atr ?? "-"}");
    };
    await monitor.StartAsync(cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Monitoring smart card readers. Press Ctrl+C to stop.");
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }

    await monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
    return 0;
}

static async Task<int> ShowDiagnosticsAsync(ISmartCardReaderService service, IPcscPlatform platform, CancellationToken cancellationToken)
{
    Console.WriteLine($"OS Version: {Environment.OSVersion}");
    Console.WriteLine($"Process Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($".NET Runtime Version: {Environment.Version}");
    Console.WriteLine("Smart Card Service Status: checked through PC/SC context");
    Console.WriteLine($"Agent Version: {typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"}");

    var exitCode = await ShowReadersAsync(service, cancellationToken).ConfigureAwait(false);
    var readers = await platform.ListReadersAsync(cancellationToken).ConfigureAwait(false);
    foreach (var reader in readers)
    {
        var state = await platform.GetReaderStateAsync(reader, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Diagnostics ReaderName=\"{state.ReaderName}\"");
        Console.WriteLine($"  CurrentState: {state.CurrentState}");
        Console.WriteLine($"  EventState: {state.EventState}");
        Console.WriteLine($"  ATR bytes: {(state.Atr is { Length: > 0 } ? AtrFormatter.ToHex(state.Atr) : "-")}");
        Console.WriteLine($"  ATR length: {state.AtrLength}");
    }

    return exitCode == 2 ? 0 : exitCode;
}

static async Task<int> ReadThaiCardAsync(IThaiCardDataProvider thaiCardProvider, ISmartCardReaderService service, string? readerName, CancellationToken cancellationToken)
{
    var resolvedReader = await ResolveReaderAsync(service, readerName, cancellationToken).ConfigureAwait(false);
    try
    {
        _ = await thaiCardProvider.ReadCitizenIdAsync(new ThaiCardReadContext(Guid.NewGuid().ToString("N"), resolvedReader), cancellationToken).ConfigureAwait(false);
        return 0;
    }
    catch (ThaiCardProtocolNotConfiguredException)
    {
        Console.WriteLine("ยังไม่ได้กำหนด Provider สำหรับอ่านข้อมูลบัตรประชาชนไทย");
        Console.WriteLine("การตรวจพบเครื่องอ่านและบัตรยังทำงานได้ตามปกติ");
        return 5;
    }
}

static async Task<string> ResolveReaderAsync(ISmartCardReaderService service, string? readerName, CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(readerName))
    {
        return readerName;
    }

    var readers = await service.GetReadersAsync(cancellationToken).ConfigureAwait(false);
    return readers.FirstOrDefault()?.Name ?? throw new ReaderNotFoundException("<default>");
}

static string? GetOption(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

static int ShowUsage()
{
    Console.WriteLine("ThaiIdCardAgent.Console readers");
    Console.WriteLine("ThaiIdCardAgent.Console status [--reader \"Reader Name\"]");
    Console.WriteLine("ThaiIdCardAgent.Console atr [--reader \"Reader Name\"]");
    Console.WriteLine("ThaiIdCardAgent.Console monitor");
    Console.WriteLine("ThaiIdCardAgent.Console diagnostics");
    Console.WriteLine("ThaiIdCardAgent.Console read [--reader \"Reader Name\"]");
    return 1;
}