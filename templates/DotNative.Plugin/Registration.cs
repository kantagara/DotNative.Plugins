using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PluginTemplate;

public static class Registration
{
    public static IServiceCollection AddPPluginNativeSymbol(this IServiceCollection services)
    {
        services.TryAddSingleton<NativeService>();
        return services;
    }
}
