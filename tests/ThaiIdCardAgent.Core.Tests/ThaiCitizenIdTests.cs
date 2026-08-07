using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Core.Tests;

/// <summary>
/// All citizen IDs here are synthetic values constructed to satisfy (or deliberately fail) the
/// check-digit algorithm. No real citizen ID appears in this repository.
/// </summary>
public sealed class ThaiCitizenIdTests
{
    // Synthetic, checksum-valid. Derived by computing the check digit for the first 12 digits.
    public const string SyntheticValidId = "1101700207366";
    public const string SyntheticValidIdAlternate = "3100600445716";

    [Theory]
    [InlineData(SyntheticValidId)]
    [InlineData(SyntheticValidIdAlternate)]
    public void IsValid_SyntheticValidId_ReturnsTrue(string citizenId)
    {
        Assert.True(ThaiCitizenId.IsValid(citizenId));
        Assert.Equal(ThaiCitizenIdValidationResult.Valid, ThaiCitizenId.Validate(citizenId));
    }

    [Fact]
    public void CheckDigit_IsActuallyEnforced()
    {
        // Change only the check digit; every other digit stays valid. If the checksum were not
        // enforced this would still pass, so this is the test that proves the algorithm runs.
        var body = SyntheticValidId[..12];
        var correctCheckDigit = SyntheticValidId[12];

        for (var candidate = '0'; candidate <= '9'; candidate++)
        {
            var value = body + candidate;
            Assert.Equal(candidate == correctCheckDigit, ThaiCitizenId.IsValid(value));
        }
    }

    [Theory]
    [InlineData("110170020736")]      // 12 digits
    [InlineData("11017002073666")]    // 14 digits
    [InlineData("")]
    public void InvalidLength_IsRejected(string citizenId)
    {
        Assert.False(ThaiCitizenId.IsValid(citizenId));
        var reason = ThaiCitizenId.Validate(citizenId);
        Assert.True(reason is ThaiCitizenIdValidationResult.InvalidLength or ThaiCitizenIdValidationResult.Missing, reason.ToString());
    }

    [Theory]
    [InlineData("110170020736X")]
    [InlineData("1-1017-00207-36-6")]  // separators are NOT stripped
    [InlineData("110170020736 ")]
    [InlineData("๑๑๐๑๗๐๐๒๐๗๓๖๖")]     // Thai digits are not normalized into validity
    public void NonDigitCharacters_AreRejectedAndNeverNormalized(string citizenId)
    {
        Assert.False(ThaiCitizenId.IsValid(citizenId));
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.False(ThaiCitizenId.IsValid(null));
        Assert.Equal(ThaiCitizenIdValidationResult.Missing, ThaiCitizenId.Validate(null));
    }

    [Fact]
    public void Validate_DistinguishesChecksumFailureFromMalformedInput()
    {
        Assert.Equal(ThaiCitizenIdValidationResult.InvalidChecksum, ThaiCitizenId.Validate(SyntheticValidId[..12] + NextDigit(SyntheticValidId[12])));
        Assert.Equal(ThaiCitizenIdValidationResult.NonDigit, ThaiCitizenId.Validate("110170020736X"));
        Assert.Equal(ThaiCitizenIdValidationResult.InvalidLength, ThaiCitizenId.Validate("1"));
    }

    [Fact]
    public void ValidationResult_NeverContainsAnyPartOfTheValue()
    {
        // The reason is an enum precisely so a rejected identifier cannot leak through an error path.
        foreach (var value in new[] { SyntheticValidId[..12] + NextDigit(SyntheticValidId[12]), "110170020736X", "1" })
        {
            var reason = ThaiCitizenId.Validate(value).ToString();
            Assert.DoesNotContain(value, reason, StringComparison.Ordinal);
            Assert.DoesNotContain(value[..Math.Min(4, value.Length)], reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MaskCitizenId_HidesTheMiddleDigits()
    {
        var masked = new PiiRedactor().MaskCitizenId(SyntheticValidId);

        Assert.DoesNotContain(SyntheticValidId, masked, StringComparison.Ordinal);
        Assert.Contains("xxxxx", masked, StringComparison.Ordinal);
        // Digits 6-10 are the ones that must not survive masking.
        Assert.DoesNotContain(SyntheticValidId.Substring(5, 5), masked, StringComparison.Ordinal);
    }

    private static char NextDigit(char digit) => digit == '9' ? '0' : (char)(digit + 1);
}
