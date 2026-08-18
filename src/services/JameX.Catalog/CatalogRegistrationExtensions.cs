using JameX.Catalog.Caching;
using JameX.Catalog.Data;
using JameX.Catalog.EventHandlers;
using JameX.Catalog.Repositories;
using JameX.Catalog.Services;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Hosting;
using StackExchange.Redis;

namespace JameX.Catalog;

public static class CatalogRegistrationExtensions
{
    public static IServiceCollection AddCatalogServices(this IServiceCollection services)
    {
        services.AddScoped<IVideoRepository, VideoRepository>();
        services.AddScoped<IVideoQueryService, VideoQueryService>();
        services.AddScoped<IVideoWriteService, VideoWriteService>();

        // All three share the same scoped CatalogDbContext, which is what lets
        // a business change, its outbox row and its inbox claim commit as one
        // transaction rather than three.
        services.AddScoped<IUnitOfWork, UnitOfWork<CatalogDbContext>>();
        services.AddScoped<IOutbox, Outbox<CatalogDbContext>>();

        // The relay that drains outbox_messages to SNS. Safe to run on every
        // replica — it claims rows with FOR UPDATE SKIP LOCKED.
        services.AddHostedService<OutboxDispatcher<CatalogDbContext>>();

        // Redis is registered by ServiceDefaults only when a connection string
        // is present. Resolving it here rather than demanding it lets the
        // service start and serve correctly with no cache at all — losing the
        // cache must cost latency, not availability.
        services.AddScoped<IVideoCache>(provider =>
        {
            var redis = provider.GetService<IConnectionMultiplexer>();

            if (redis is null)
            {
                provider.GetRequiredService<ILogger<NullVideoCache>>()
                    .LogWarning("No Redis configured; Catalog reads will always hit Postgres");
                return new NullVideoCache();
            }

            return new RedisVideoCache(redis, provider.GetRequiredService<ILogger<RedisVideoCache>>());
        });

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
