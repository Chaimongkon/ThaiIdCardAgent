namespace ThaiIdCardAgent.Pcsc;

public static class AtrFormatter
{
    public static string ToHex(byte[] atr) => string.Join("-", atr.Select(value => value.ToString("X2")));
}