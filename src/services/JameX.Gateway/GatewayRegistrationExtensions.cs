using JameX.Gateway.Clients;
using JameX.Gateway.Configuration;
using JameX.Gateway.Services;
using Microsoft.Extensions.Options;

namespace JameX.Gateway;

public static class GatewayRegistrationExtensions
{
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BffClientOptions>(configuration.GetSection(BffClientOptions.SectionName));

        services.AddScoped<IWatchAggregationService, WatchAggregationService>();

        services.AddHttpClient<ICatalogReadClient, CatalogReadClient>((provider, http) =>
            http.BaseAddress = new Uri(Options(provider).CatalogBaseUrl));

        services.AddHttpClient<IEngagementReadClient, EngagementReadClient>((provider, http) =>
            http.BaseAddress = new Uri(Options(provider).EngagementBaseUrl));

        services.AddHttpClient<IIdentityReadClient, IdentityReadClient>((provider, http) =>
            http.BaseAddress = new Uri(Options(provider).IdentityBaseUrl));

        return services;
    }

    private static BffClientOptions Options(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<BffClientOptions>>().Value;
}
