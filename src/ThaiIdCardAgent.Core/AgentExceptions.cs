namespace ThaiIdCardAgent.Core;

public abstract class AgentException : Exception
{
    protected AgentException(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class SmartCardServiceUnavailableException : AgentException
{
    public SmartCardServiceUnavailableException(string message = "Windows Smart Card Service is unavailable.", Exception? innerException = null)
        : base(message, AgentErrorCodes.SmartCardServiceUnavailable, innerException)
    {
    }
}

public sealed class ReaderNotFoundException : AgentException
{
    public ReaderNotFoundException(string readerName)
        : base($"Smart card reader was not found: {readerName}", AgentErrorCodes.ReaderNotFound)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

public sealed class ReaderSelectionRequiredException : AgentException
{
    public ReaderSelectionRequiredException()
        : base("Reader selection is required because multiple smart card readers are available.", AgentErrorCodes.ReaderSelectionRequired)
    {
    }
}

public sealed class CardNotPresentException : AgentException
{
    public CardNotPresentException(string readerName)
        : base($"No card is present in reader: {readerName}", AgentErrorCodes.CardNotPresent)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

public sealed class CardRemovedException : AgentException
{
    public CardRemovedException(string readerName)
        : base($"The card was removed from reader: {readerName}", AgentErrorCodes.CardRemoved)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

public sealed class SmartCardBusyException : AgentException
{
    public SmartCardBusyException(string readerName)
        : base($"Reader is busy: {readerName}", AgentErrorCodes.AgentBusy)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

public sealed class SmartCardCommunicationException : AgentException
{
    public SmartCardCommunicationException(string message, Exception? innerException = null)
        : base(message, AgentErrorCodes.ReaderUnavailable, innerException)
    {
    }
}

public sealed class ThaiCardProtocolNotConfiguredException : AgentException
{
    public ThaiCardProtocolNotConfiguredException()
        : base("ยังไม่ได้กำหนด Provider สำหรับอ่านข้อมูลบัตรประชาชนไทย", AgentErrorCodes.ThaiCardProtocolNotConfigured)
    {
    }
}

/// <summary>The card read exceeded its allotted time.</summary>
public sealed class CardReadTimeoutException : AgentException
{
    public CardReadTimeoutException(string readerName)
        : base($"Reading the card timed out on reader: {readerName}", AgentErrorCodes.CardReadTimeout)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

/// <summary>
/// Communication with the card failed. The message never carries card content or APDU payloads.
/// </summary>
public sealed class CardCommunicationException : AgentException
{
    public CardCommunicationException(string readerName, Exception? innerException = null)
        : base($"Communication with the card failed on reader: {readerName}", AgentErrorCodes.CardCommunicationError, innerException)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

/// <summary>
/// The card returned data that failed validation. Fails closed: the value is never repaired, and
/// neither the value nor any part of it appears in the message.
/// </summary>
public sealed class CardDataInvalidException : AgentException
{
    public CardDataInvalidException(ThaiCitizenIdValidationResult reason)
        : base($"Card data failed validation and was rejected ({reason}).", AgentErrorCodes.CardDataInvalid)
    {
        Reason = reason;
    }

    public ThaiCitizenIdValidationResult Reason { get; }
}

/// <summary>The card was withdrawn while the read was in progress.</summary>
public sealed class CardRemovedDuringReadException : AgentException
{
    public CardRemovedDuringReadException(string readerName, Exception? innerException = null)
        : base($"The card was removed from reader '{readerName}' during the read.", AgentErrorCodes.CardRemovedDuringRead, innerException)
    {
        ReaderName = readerName;
    }

    public string ReaderName { get; }
}

/// <summary>
/// A provider is configured but cannot service the request (driver, SDK, or device unavailable).
/// Distinct from <see cref="ThaiCardProtocolNotConfiguredException"/>, which means no provider exists.
/// </summary>
public sealed class ThaiCardProviderUnavailableException : AgentException
{
    public ThaiCardProviderUnavailableException(string providerName, Exception? innerException = null)
        : base($"Thai card provider '{providerName}' is unavailable.", AgentErrorCodes.ProviderUnavailable, innerException)
    {
        ProviderName = providerName;
    }

    public string ProviderName { get; }
}

public static class AgentErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string ReaderNotFound = "READER_NOT_FOUND";
    public const string AgentBusy = "AGENT_BUSY";
    public const string CardRemoved = "CARD_REMOVED";
    public const string CardNotPresent = "CARD_NOT_PRESENT";
    public const string ReaderSelectionRequired = "READER_SELECTION_REQUIRED";
    public const string SmartCardServiceUnavailable = "SMART_CARD_SERVICE_UNAVAILABLE";
    public const string ReaderUnavailable = "READER_UNAVAILABLE";
    public const string Timeout = "TIMEOUT";
    public const string ThaiCardProtocolNotConfigured = "THAI_CARD_PROTOCOL_NOT_CONFIGURED";
    public const string CardReadTimeout = "CARD_READ_TIMEOUT";
    public const string CardCommunicationError = "CARD_COMMUNICATION_ERROR";
    public const string CardDataInvalid = "CARD_DATA_INVALID";
    public const string CardRemovedDuringRead = "CARD_REMOVED_DURING_READ";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string InvalidConfiguration = "INVALID_CONFIGURATION";
    public const string InternalError = "INTERNAL_ERROR";
}
