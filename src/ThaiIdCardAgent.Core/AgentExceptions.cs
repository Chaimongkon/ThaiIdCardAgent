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
        : base(message, AgentErrorCodes.SmartCardCommunicationError, innerException)
    {
    }
}

public sealed class ThaiCardProtocolNotConfiguredException : AgentException
{
    public ThaiCardProtocolNotConfiguredException()
        : base("Thai ID card protocol provider is not configured.", AgentErrorCodes.ThaiCardProtocolNotConfigured)
    {
    }
}

public static class AgentErrorCodes
{
    public const string SmartCardServiceUnavailable = "SMART_CARD_SERVICE_UNAVAILABLE";
    public const string ReaderNotFound = "READER_NOT_FOUND";
    public const string CardNotPresent = "CARD_NOT_PRESENT";
    public const string CardRemoved = "CARD_REMOVED";
    public const string AgentBusy = "AGENT_BUSY";
    public const string SmartCardCommunicationError = "SMART_CARD_COMMUNICATION_ERROR";
    public const string ThaiCardProtocolNotConfigured = "THAI_CARD_PROTOCOL_NOT_CONFIGURED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Timeout = "TIMEOUT";
    public const string InvalidConfiguration = "INVALID_CONFIGURATION";
    public const string UnexpectedError = "UNEXPECTED_ERROR";
}