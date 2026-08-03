using System.Text.RegularExpressions;

namespace ThaiIdCardAgent.Core;

public interface IPiiRedactor
{
    string MaskCitizenId(string citizenId);

    string Redact(string value);
}

public sealed partial class PiiRedactor : IPiiRedactor
{
    public string MaskCitizenId(string citizenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(citizenId);
        var digits = DigitsOnlyRegex().Replace(citizenId, string.Empty);
        if (digits.Length != 13)
        {
            return Redact(citizenId);
        }

        return $"{digits[0]}-{digits.Substring(1, 4)}-xxxxx-{digits.Substring(10, 2)}-{digits[12]}";
    }

    public string Redact(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : "[REDACTED]";

    [GeneratedRegex("\\D")]
    private static partial Regex DigitsOnlyRegex();
}