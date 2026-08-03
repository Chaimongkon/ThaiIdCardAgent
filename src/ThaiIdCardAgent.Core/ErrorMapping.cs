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

        return new AgentError(AgentErrorCodes.InternalError, "Unexpected agent error.", includeTechnicalDetail ? exception.ToString() : null);
    }
}

public sealed record AgentErrorResponse(bool Success, object? Data, AgentError Error, string RequestId)
{
    public static AgentErrorResponse FromError(string requestId, AgentError error) => new(false, null, error, requestId);
}
