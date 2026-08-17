using JameX.Catalog.Data;
using JameX.Catalog.EventHandlers;
using JameX.Catalog.Repositories;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Hosting;

namespace JameX.Catalog;

public static class CatalogRegistrationExtensions
{
    public static IServiceCollection AddCatalogServices(this IServiceCollection services)
    {
        services.AddScoped<IVideoRepository, VideoRepository>();

        // Bound to CatalogDbContext, so the inbox row and the business change
        // share one context and therefore one transaction.
        services.AddScoped<IInboxUnitOfWork, InboxUnitOfWork<CatalogDbContext>>();

        // One handler per event type this service subscribes to. The consumer
        // loop dispatches on IEventHandler.EventType, and these three must match
        // the queue's SNS filter policy in infra/localstack/init — a handler
        // with no matching filter never fires, and a filter with no handler logs
        // a warning and discards.
        services.AddEventHandler<VideoUploadedHandler>();
        services.AddEventHandler<VideoEncodedHandler>();
        services.AddEventHandler<VideoEncodingFailedHandler>();

        // Note: no IEventDeduplicator here on purpose. Catalog owns a relational
        // store, so it uses the durable inbox instead — the Redis deduplicator
        // cannot join this transaction and would only add a way to lose events.
        // See RedisEventDeduplicator's remarks.

        return services;
    }
}
