using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace JameX.ServiceDefaults.Messaging;

/// <summary>
/// Best-effort duplicate filter backed by Redis <c>SET NX</c>.
/// <para>
/// This narrows the at-least-once window; it does not close it. Redis is a
/// cache — it can evict under memory pressure and it is not part of the
/// handler's transaction, so a crash between "mark seen" and "apply change"
/// loses the change permanently. A service that owns a relational database
/// should use the inbox pattern instead: insert the event id into a
/// <c>processed_events</c> table inside the same transaction as the write, and
/// let the primary key conflict reject the duplicate. Then the check and the
/// effect commit or roll back together.
/// </para>
/// <para>
/// Use this only where no such transaction exists, and keep handlers
/// genuinely idempotent regardless.
/// </para>
/// </summary>
public sealed class RedisEventDeduplicator : IEventDeduplicator
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisEventDeduplicator> _logger;
    private readonly string _serviceName;

    public RedisEventDeduplicator(
        IConnectionMultiplexer redis,
        ILogger<RedisEventDeduplicator> logger,
        IServiceIdentity identity)
    {
        _redis = redis;
        _logger = logger;
        _serviceName = identity.ServiceName;
    }

    public async Task<bool> TryBeginAsync(Guid eventId, CancellationToken ct = default)
    {
        // Keyed per service: the same event is legitimately delivered to
        // several services, and each must process it exactly once of its own.
        var key = $"jamex:dedupe:{_serviceName}:{eventId:N}";

        try
        {
            var isFirst = await _redis.GetDatabase()
                .StringSetAsync(key, DateTimeOffset.UtcNow.ToString("O"), Retention, When.NotExists);

            if (!isFirst)
                _logger.LogInformation("Skipping duplicate event {EventId} in {Service}", eventId, _serviceName);

            return isFirst;
        }
        catch (Exception ex)
        {
            // Fail open. Losing the event is worse than applying it twice, and
            // handlers are required to be idempotent anyway.
            _logger.LogWarning(ex, "Deduplication check failed for {EventId}; processing anyway", eventId);
            return true;
        }
    }
}
