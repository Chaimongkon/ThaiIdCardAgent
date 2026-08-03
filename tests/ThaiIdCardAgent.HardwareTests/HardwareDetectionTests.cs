using Microsoft.Extensions.Options;
using ThaiIdCardAgent.Pcsc;

namespace ThaiIdCardAgent.HardwareTests;

public sealed class HardwareDetectionTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Readers_CanBeDetected_WhenHardwareTestsAreEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("THAI_ID_AGENT_HARDWARE_TESTS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var service = new PcscSmartCardReaderService(new WinSCardPlatform(), Options.Create(new PcscOptions()));
        var readers = await service.GetReadersAsync();

        Assert.NotEmpty(readers);
    }
}