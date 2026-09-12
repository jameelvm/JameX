using JameX.Search.Clients;
using JameX.Search.Configuration;
using JameX.Search.EventHandlers;
using JameX.Search.Repositories;
using JameX.Search.Services;
using JameX.ServiceDefaults.Hosting;
using Microsoft.Extensions.Options;

namespace JameX.Search;

public static class SearchRegistrationExtensions
{
    public static IServiceCollection AddSearchServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.SectionName));
        services.Configure<CatalogClientOptions>(configuration.GetSection(CatalogClientOptions.SectionName));

        services.AddScoped<ISearchIndexRepository, SearchIndexRepository>();
        services.AddScoped<ISearchQueryService, SearchQueryService>();

        services.AddHttpClient<ICatalogClient, CatalogClient>((provider, http) =>
        {
            var catalogOptions = provider.GetRequiredService<IOptions<CatalogClientOptions>>().Value;
            http.BaseAddress = new Uri(catalogOptions.BaseUrl);
        });

        // One handler per event type this service subscribes to — must match
        // the jamex-search-events filter policy in infra/localstack/init.
        // No IInboxUnitOfWork here: Search owns no relational store, so it
        // uses the Redis IEventDeduplicator AddJameXServiceDefaults already
        // registers — same as Encoder.
        services.AddEventHandler<VideoEncodedHandler>();
        services.AddEventHandler<VideoDeletedHandler>();

        return services;
    }
}
