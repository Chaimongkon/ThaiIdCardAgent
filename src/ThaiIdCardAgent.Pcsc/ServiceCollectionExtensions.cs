using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.Pcsc;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPcscSmartCardServices(this IServiceCollection services)
    {
        services.AddOptions<PcscOptions>().BindConfiguration("Pcsc").Validate(options => options.TimeoutSeconds is > 0 and <= 120, "Pcsc:TimeoutSeconds must be between 1 and 120.");
        services.TryAddSingleton<IPcscPlatform, WinSCardPlatform>();
        services.TryAddSingleton<ISmartCardReaderService, PcscSmartCardReaderService>();
        services.TryAddTransient<ISmartCardMonitor, PollingSmartCardMonitor>();
        return services;
    }
}