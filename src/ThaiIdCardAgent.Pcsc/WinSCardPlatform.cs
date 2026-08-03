using System.Runtime.InteropServices;
using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

public sealed class WinSCardPlatform : IPcscPlatform
{
    public Task<IReadOnlyList<string>> ListReadersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = EstablishContext();
        try
        {
            var length = 0;
            var result = NativeMethods.SCardListReaders(context, null, null, ref length);
            if (result == NativeMethods.SCARD_E_NO_READERS_AVAILABLE)
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            ThrowIfFailure(result, "Unable to list smart card readers.");
            var buffer = new char[length];
            result = NativeMethods.SCardListReaders(context, null, buffer, ref length);
            ThrowIfFailure(result, "Unable to list smart card readers.");
            var readers = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Task.FromResult<IReadOnlyList<string>>(readers);
        }
        finally
        {
            _ = NativeMethods.SCardReleaseContext(context);
        }
    }

    public async Task<PcscReaderState> GetReaderStateAsync(string readerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerName);
        cancellationToken.ThrowIfCancellationRequested();
        var readers = await ListReadersAsync(cancellationToken).ConfigureAwait(false);
        var resolvedReaderName = readers.FirstOrDefault(reader => string.Equals(reader, readerName, StringComparison.OrdinalIgnoreCase));
        if (resolvedReaderName is null)
        {
            throw new ReaderNotFoundException(readerName);
        }

        var context = EstablishContext();
        try
        {
            var readerStates = new[]
            {
                new NativeMethods.SCARD_READERSTATE
                {
                    ReaderName = resolvedReaderName,
                    CurrentState = (uint)PcscState.Unaware,
                    EventState = (uint)PcscState.Unaware,
                    Atr = new byte[NativeMethods.SCARD_ATR_LENGTH]
                }
            };

            var result = NativeMethods.SCardGetStatusChange(context, 0, readerStates, readerStates.Length);
            ThrowIfFailure(result, "Unable to read smart card reader state.");

            var state = readerStates[0];
            var currentState = (PcscState)state.CurrentState;
            var eventState = (PcscState)state.EventState;
            var presenceStatus = PcscStateMapper.ToPresenceStatus(eventState);
            var atr = PcscStateMapper.TrimAtr(state.Atr, (int)state.AtrLength);
            return new PcscReaderState(
                resolvedReaderName,
                presenceStatus,
                atr.Length > 0 ? atr : null,
                DateTimeOffset.UtcNow,
                currentState,
                eventState,
                (int)state.AtrLength);
        }
        finally
        {
            _ = NativeMethods.SCardReleaseContext(context);
        }
    }

    private static IntPtr EstablishContext()
    {
        var result = NativeMethods.SCardEstablishContext(NativeMethods.SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out var context);
        if (result == NativeMethods.SCARD_E_NO_SERVICE)
        {
            throw new SmartCardServiceUnavailableException();
        }

        ThrowIfFailure(result, "Unable to establish PC/SC system context.");
        return context;
    }

    private static void ThrowIfFailure(int result, string message)
    {
        if (result == NativeMethods.SCARD_S_SUCCESS)
        {
            return;
        }

        if (result == NativeMethods.SCARD_E_NO_SERVICE)
        {
            throw new SmartCardServiceUnavailableException(message);
        }

        if (result is NativeMethods.SCARD_E_READER_UNAVAILABLE or NativeMethods.SCARD_E_UNKNOWN_READER)
        {
            throw new SmartCardCommunicationException($"{message} Reader unavailable. PC/SC error 0x{result:X8}.");
        }

        throw new SmartCardCommunicationException($"{message} PC/SC error 0x{result:X8}.");
    }

    private static partial class NativeMethods
    {
        public const int SCARD_S_SUCCESS = 0;
        public const int SCARD_E_NO_SERVICE = -2146435043;
        public const int SCARD_E_NO_READERS_AVAILABLE = -2146435026;
        public const int SCARD_E_UNKNOWN_READER = -2146435063;
        public const int SCARD_E_READER_UNAVAILABLE = -2146435045;
        public const uint SCARD_SCOPE_SYSTEM = 2;
        public const int SCARD_ATR_LENGTH = 36;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SCARD_READERSTATE
        {
            [MarshalAs(UnmanagedType.LPTStr)]
            public string? ReaderName;

            public IntPtr UserData;

            public uint CurrentState;

            public uint EventState;

            public uint AtrLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = SCARD_ATR_LENGTH)]
            public byte[]? Atr;
        }

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardListReaders(IntPtr hContext, string? mszGroups, char[]? mszReaders, ref int pcchReaders);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardGetStatusChange(IntPtr hContext, int dwTimeout, [In, Out] SCARD_READERSTATE[] rgReaderStates, int cReaders);

        [DllImport("winscard.dll")]
        public static extern int SCardReleaseContext(IntPtr hContext);
    }
}