using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.ThaiCard;

public sealed record ThaiIdCardData(
    string CitizenId,
    string? ThaiTitle,
    string? ThaiFirstName,
    string? ThaiMiddleName,
    string? ThaiLastName,
    string? EnglishTitle,
    string? EnglishFirstName,
    string? EnglishMiddleName,
    string? EnglishLastName,
    DateOnly? BirthDate,
    string? Gender,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    string? Address,
    byte[]? Photo);

public sealed record ThaiIdCardReadOptions(
    bool ReadCitizenId = true,
    bool ReadThaiName = true,
    bool ReadEnglishName = false,
    bool ReadBirthDate = false,
    bool ReadAddress = false,
    bool ReadIssueAndExpiryDates = false,
    bool ReadPhoto = false);

public interface IThaiIdCardReader
{
    Task<ThaiIdCardData> ReadAsync(string readerName, ThaiIdCardReadOptions options, CancellationToken cancellationToken = default);
}

public sealed class NotConfiguredThaiIdCardReader : IThaiIdCardReader
{
    public Task<ThaiIdCardData> ReadAsync(string readerName, ThaiIdCardReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerName);
        ArgumentNullException.ThrowIfNull(options);
        throw new ThaiCardProtocolNotConfiguredException();
    }
}