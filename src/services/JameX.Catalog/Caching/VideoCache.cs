using JameX.Catalog.Validation;
using JameX.Contracts.Dtos;
using JameX.ServiceDefaults.Messaging;
using StackExchange.Redis;

namespace JameX.Catalog.Caching;

/// <summary>
/// Cache-aside for the watch page — the doc's Memcached tier, standing in as
/// Redis here.
/// <para>
/// Cache-aside means the application owns the cache: read it, and on a miss
/// read the database and populate it. The database never writes to the cache
/// itself and never knows it exists. The alternative — write-through — keeps
/// the two in lockstep at the cost of making every write pay for a cache
/// update, which is the wrong trade for data read far more often than written.
/// </para>
/// </summary>
public interface IVideoCache
{
    Task<VideoDetail?> GetAsync(Guid videoId, CancellationToken ct);

    Task SetAsync(VideoDetail detail, CancellationToken ct);

    /// <summary>
    /// Removes the entry — <b>delete, never overwrite</b>.
    /// <para>
    /// Writing the new value into the cache from a writer looks tidier but
    /// races: two concurrent updates can land in the cache in the opposite
    /// order to the database, leaving the cache permanently disagreeing with
    /// the row. Deleting means the next reader repopulates from the committed
    /// truth, so the worst case is one extra database read.
    /// </para>
    /// </summary>
    Task InvalidateAsync(Guid videoId, CancellationToken ct);
}

public sealed class RedisVideoCache(
    IConnectionMultiplexer redis,
    ILogger<RedisVideoCache> logger) : IVideoCache
{
    private static string Key(Guid videoId) => $"jamex:catalog:video:{videoId:N}";

    public async Task<VideoDetail?> GetAsync(Guid videoId, CancellationToken ct)
    {
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(Key(videoId));
            return cached.IsNullOrEmpty
                ? null
                : JameXJson.Deserialize<VideoDetail>(cached!);
        }
        catch (Exception ex)
        {
            // A cache must degrade latency, never availability. Treating a
            // Redis fault as a miss means the request still succeeds from
            // Postgres — slower, but served.
            logger.LogWarning(ex, "Cache read failed for video {VideoId}; falling back to the database", videoId);
            return null;
        }
    }

    public async Task SetAsync(VideoDetail detail, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(
                Key(detail.VideoId),
                JameXJson.Serialize(detail),
                CatalogRules.VideoCacheTtl);
        }
        catch (Exception ex)
        {
            // Failing to populate is harmless — the next read simply misses again.
            logger.LogWarning(ex, "Cache write failed for video {VideoId}", detail.VideoId);
        }
    }

    public async Task InvalidateAsync(Guid videoId, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(Key(videoId));
            logger.LogDebug("Invalidated cache for video {VideoId}", videoId);
        }
        catch (Exception ex)
        {
            // This one genuinely matters: a failed invalidation serves stale
            // data until the TTL expires. Bounded by CatalogRules.VideoCacheTtl,
            // which is why that TTL is minutes rather than hours.
            logger.LogError(ex, "Cache invalidation FAILED for video {VideoId}; stale until TTL expiry", videoId);
        }
    }
}

/// <summary>
/// Used when no Redis connection string is configured. The service must run
/// without a cache — losing one should cost performance, not startup.
/// </summary>
public sealed class NullVideoCache : IVideoCache
{
    public Task<VideoDetail?> GetAsync(Guid videoId, CancellationToken ct) =>
        Task.FromResult<VideoDetail?>(null);

    public Task SetAsync(VideoDetail detail, CancellationToken ct) => Task.CompletedTask;

    public Task InvalidateAsync(Guid videoId, CancellationToken ct) => Task.CompletedTask;
}
