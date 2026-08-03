using System.Runtime.InteropServices;
using System.Text;
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
        if (!readers.Contains(readerName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ReaderNotFoundException(readerName);
        }

        var context = EstablishContext();
        IntPtr card = IntPtr.Zero;
        try
        {
            var connectResult = NativeMethods.SCardConnect(
                context,
                readerName,
                NativeMethods.SCARD_SHARE_SHARED,
                NativeMethods.SCARD_PROTOCOL_T0 | NativeMethods.SCARD_PROTOCOL_T1,
                out card,
                out _);

            if (connectResult is NativeMethods.SCARD_E_NO_SMARTCARD or NativeMethods.SCARD_W_REMOVED_CARD)
            {
                return new PcscReaderState(readerName, SmartCardPresenceStatus.NoCard, null, DateTimeOffset.UtcNow);
            }

            if (connectResult == NativeMethods.SCARD_E_SHARING_VIOLATION)
            {
                throw new SmartCardBusyException(readerName);
            }

            if (connectResult is NativeMethods.SCARD_E_READER_UNAVAILABLE or NativeMethods.SCARD_E_UNKNOWN_READER)
            {
                throw new ReaderNotFoundException(readerName);
            }

            ThrowIfFailure(connectResult, "Unable to connect to smart card reader.");
            var atr = ReadAtr(card, readerName);
            return new PcscReaderState(readerName, SmartCardPresenceStatus.CardPresent, atr, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (card != IntPtr.Zero)
            {
                _ = NativeMethods.SCardDisconnect(card, NativeMethods.SCARD_LEAVE_CARD);
            }

            _ = NativeMethods.SCardReleaseContext(context);
        }
    }

    private static byte[] ReadAtr(IntPtr card, string readerName)
    {
        var readerLength = 0;
        var atrLength = 64;
        var atr = new byte[atrLength];
        var result = NativeMethods.SCardStatus(card, null, ref readerLength, out var state, out _, atr, ref atrLength);
        if (result is NativeMethods.SCARD_W_REMOVED_CARD or NativeMethods.SCARD_E_NO_SMARTCARD)
        {
            throw new CardRemovedException(readerName);
        }

        ThrowIfFailure(result, "Unable to read smart card ATR.");
        if ((state & NativeMethods.SCARD_PRESENT) != NativeMethods.SCARD_PRESENT)
        {
            throw new CardNotPresentException(readerName);
        }

        Array.Resize(ref atr, atrLength);
        return atr;
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

        throw new SmartCardCommunicationException($"{message} PC/SC error 0x{result:X8}.");
    }

    private static partial class NativeMethods
    {
        public const int SCARD_S_SUCCESS = 0;
        public const int SCARD_E_NO_SERVICE = -2146435043;
        public const int SCARD_E_NO_READERS_AVAILABLE = -2146435026;
        public const int SCARD_E_NO_SMARTCARD = -2146435060;
        public const int SCARD_E_UNKNOWN_READER = -2146435063;
        public const int SCARD_E_READER_UNAVAILABLE = -2146435045;
        public const int SCARD_E_SHARING_VIOLATION = -2146435061;
        public const int SCARD_W_REMOVED_CARD = -2146434967;
        public const uint SCARD_SCOPE_SYSTEM = 2;
        public const uint SCARD_SHARE_SHARED = 2;
        public const uint SCARD_PROTOCOL_T0 = 1;
        public const uint SCARD_PROTOCOL_T1 = 2;
        public const uint SCARD_LEAVE_CARD = 0;
        public const uint SCARD_PRESENT = 0x20;

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardListReaders(IntPtr hContext, string? mszGroups, char[]? mszReaders, ref int pcchReaders);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardConnect(IntPtr hContext, string szReader, uint dwShareMode, uint dwPreferredProtocols, out IntPtr phCard, out uint pdwActiveProtocol);

        [DllImport("winscard.dll", CharSet = CharSet.Unicode)]
        public static extern int SCardStatus(IntPtr hCard, StringBuilder? mszReaderNames, ref int pcchReaderLen, out uint pdwState, out uint pdwProtocol, byte[] pbAtr, ref int pcbAtrLen);

        [DllImport("winscard.dll")]
        public static extern int SCardDisconnect(IntPtr hCard, uint dwDisposition);

        [DllImport("winscard.dll")]
        public static extern int SCardReleaseContext(IntPtr hContext);
    }
}