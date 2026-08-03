namespace ThaiIdCardAgent.Core;

public static class AgentErrorMapper
{
    public static AgentError FromException(Exception exception, bool includeTechnicalDetail)
    {
        if (exception is AgentException agentException)
        {
            return new AgentError(agentException.Code, agentException.Message, includeTechnicalDetail ? exception.ToString() : null);
        }

        if (exception is OperationCanceledException)
        {
            return new AgentError(AgentErrorCodes.Timeout, "Operation timed out.", includeTechnicalDetail ? exception.ToString() : null);
        }

        return new AgentError(AgentErrorCodes.UnexpectedError, "Unexpected agent error.", includeTechnicalDetail ? exception.ToString() : null);
    }
}

public sealed record AgentErrorResponse(string RequestId, string Code, string Message, string? TechnicalDetail = null);