namespace ThaiIdCardAgent.Core;

/// <summary>
/// Validation for the 13-digit Thai citizen identification number.
/// </summary>
/// <remarks>
/// Validation is strict and never repairs input. A value that is not exactly 13 ASCII decimal
/// digits, or whose check digit does not match, is rejected outright — separators are not stripped,
/// whitespace is not trimmed into validity, and a wrong check digit is never corrected. A card that
/// yields malformed data is a card that must fail closed, because silently "fixing" a digit would
/// produce a different person's identifier.
/// </remarks>
public static class ThaiCitizenId
{
    public const int Length = 13;

    /// <summary>
    /// True when <paramref name="value"/> is exactly 13 ASCII decimal digits with a valid check digit.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        var weightedSum = 0;
        for (var index = 0; index < Length - 1; index++)
        {
            var digit = ToDigit(value[index]);
            if (digit < 0)
            {
                return false;
            }

            // Positional weights run 13 down to 2 across the first twelve digits.
            weightedSum += digit * (Length - index);
        }

        var lastDigit = ToDigit(value[Length - 1]);
        if (lastDigit < 0)
        {
            return false;
        }

        var expectedCheckDigit = (11 - (weightedSum % 11)) % 10;
        return lastDigit == expectedCheckDigit;
    }

    /// <summary>
    /// Describes why a value is not a valid citizen ID, for structured error reporting.
    /// The returned reason never contains any part of the value itself.
    /// </summary>
    public static ThaiCitizenIdValidationResult Validate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return ThaiCitizenIdValidationResult.Missing;
        }

        if (value.Length != Length)
        {
            return ThaiCitizenIdValidationResult.InvalidLength;
        }

        foreach (var character in value)
        {
            if (ToDigit(character) < 0)
            {
                return ThaiCitizenIdValidationResult.NonDigit;
            }
        }

        return IsValid(value) ? ThaiCitizenIdValidationResult.Valid : ThaiCitizenIdValidationResult.InvalidChecksum;
    }

    // Deliberately ASCII-only: Thai and Arabic-Indic digit forms are not accepted, because
    // accepting them would mean normalizing card data rather than rejecting it.
    private static int ToDigit(char character) => character is >= '0' and <= '9' ? character - '0' : -1;
}

public enum ThaiCitizenIdValidationResult
{
    Valid,
    Missing,
    InvalidLength,
    NonDigit,
    InvalidChecksum
}
