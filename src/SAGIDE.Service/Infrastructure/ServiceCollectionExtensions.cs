using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SAGIDE.Service.Infrastructure;

/// <summary>
/// Helper that eliminates the repetitive bind-then-register pattern for config objects.
/// Usage: builder.Services.AddConfiguredSingleton&lt;TimeoutConfig&gt;(configuration, "SAGIDE:Timeouts");
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredSingleton<T>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionPath) where T : class, new()
    {
        var instance = new T();
        configuration.GetSection(sectionPath).Bind(instance);
        return services.AddSingleton(instance);
    }
}
