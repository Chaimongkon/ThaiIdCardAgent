using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void MaskCitizenId_UsesRequiredFormat()
    {
        var redactor = new PiiRedactor();

        var masked = redactor.MaskCitizenId("1234567890123");

        Assert.Equal("1-2345-xxxxx-12-3", masked);
    }

    [Fact]
    public void ErrorMapper_DoesNotIncludeTechnicalDetail_WhenDisabled()
    {
        var error = AgentErrorMapper.FromException(new ReaderNotFoundException("Reader A"), includeTechnicalDetail: false);

        Assert.Equal(AgentErrorCodes.ReaderNotFound, error.Code);
        Assert.Null(error.TechnicalDetail);
    }

    [Fact]
    public void OperationResult_CreatesSuccessAndFailureShapes()
    {
        var success = OperationResult<string>.Ok("ok");
        var failure = OperationResult<string>.Fail(new AgentError("ERR", "Broken"));

        Assert.True(success.Success);
        Assert.Equal("ok", success.Data);
        Assert.False(failure.Success);
        Assert.Equal("ERR", failure.Error?.Code);
    }
}