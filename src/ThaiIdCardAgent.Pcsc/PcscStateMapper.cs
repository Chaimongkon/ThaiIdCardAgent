using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

[Flags]
public enum PcscState : uint
{
    Unaware = 0x0000,
    Ignore = 0x0001,
    Changed = 0x0002,
    Unknown = 0x0004,
    Unavailable = 0x0008,
    Empty = 0x0010,
    Present = 0x0020,
    AtrMatch = 0x0040,
    Exclusive = 0x0080,
    InUse = 0x0100,
    Mute = 0x0200,
    Unpowered = 0x0400
}

public static class PcscStateMapper
{
    public static SmartCardPresenceStatus ToPresenceStatus(PcscState eventState)
    {
        if (HasFlag(eventState, PcscState.Unavailable) || HasFlag(eventState, PcscState.Unknown))
        {
            return SmartCardPresenceStatus.ReaderUnavailable;
        }

        if (HasFlag(eventState, PcscState.Present))
        {
            return SmartCardPresenceStatus.CardPresent;
        }

        if (HasFlag(eventState, PcscState.Empty))
        {
            return SmartCardPresenceStatus.NoCard;
        }
        if (HasFlag(eventState, PcscState.Mute))
        {
            return SmartCardPresenceStatus.CardMute;
        }

        if (HasFlag(eventState, PcscState.Unpowered))
        {
            return SmartCardPresenceStatus.CardUnpowered;
        }

        return SmartCardPresenceStatus.Unknown;
    }

    public static bool IsCardPresent(PcscState eventState) => HasFlag(eventState, PcscState.Present);

    public static bool IsReaderAvailable(PcscState eventState) =>
        !HasFlag(eventState, PcscState.Unavailable) && !HasFlag(eventState, PcscState.Unknown);

    public static bool HasChanged(PcscState eventState) => HasFlag(eventState, PcscState.Changed);

    public static byte[] TrimAtr(byte[]? atrBuffer, int atrLength)
    {
        if (atrBuffer is null || atrLength <= 0)
        {
            return [];
        }

        var length = Math.Min(atrLength, atrBuffer.Length);
        var atr = new byte[length];
        Array.Copy(atrBuffer, atr, length);
        return atr;
    }

    private static bool HasFlag(PcscState value, PcscState flag) => (value & flag) == flag;
}
