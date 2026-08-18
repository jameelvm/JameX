using JameX.ServiceDefaults.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JameX.ServiceDefaults.Data;

/// <summary>
/// Publishes rows from <c>outbox_messages</c> to SNS and marks them sent.
/// <para>
/// This is the second half of the transactional outbox. The writer made the
/// intention to publish durable; this makes it happen. Between the two, the
/// event is safe from any crash — the worst case is that it is published
/// slightly late, or published twice, and consumers already handle the latter
/// through their inbox.
/// </para>
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    /// <summary>
    /// Small on purpose. The batch is published while a database transaction
    /// holds locks on those rows, so a large batch means holding locks across a
    /// long run of network calls.
    /// </summary>
    private const int BatchSize = 20;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After this many failures a row is left alone and logged loudly. Retrying
    /// a poison message forever would block nothing — the query skips it — but
    /// it would bury the logs and hide the real problem.
    /// </summary>
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox dispatcher started (every {Seconds}s, batch {Batch})",
            PollInterval.TotalSeconds, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await DispatchBatchAsync(stoppingToken);

                // Nothing waiting — wait before asking again. When there is
                // work, loop straight round so a backlog drains quickly.
                if (sent == 0) await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch loop error; backing off");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        // AddJameXPostgres enables retry-on-failure, which installs an
        // execution strategy. Any code that opens its own transaction must run
        // through that strategy, or EF throws — because a retry has to replay
        // the whole transaction, not resume it halfway.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // FOR UPDATE SKIP LOCKED is what makes this safe to run on every
            // replica at once. Each dispatcher locks the rows it takes, and
            // SKIP LOCKED makes the others step over those rows instead of
            // blocking on them. Without it, three replicas would each publish
            // the same batch.
            var batch = await db.Set<OutboxMessage>()
                .FromSqlRaw(
                    """
                    SELECT * FROM outbox_messages
                    WHERE published_at IS NULL AND attempt_count < {0}
                    ORDER BY id
                    LIMIT {1}
                    FOR UPDATE SKIP LOCKED
                    """,
                    MaxAttempts, BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return 0;
            }

            var published = 0;

            foreach (var message in batch)
            {
                try
                {
                    await publisher.PublishEnvelopeAsync(message.EventType, message.Payload, ct);

                    message.PublishedAt = DateTimeOffset.UtcNow;
                    published++;
                }
                catch (Exception ex)
                {
                    // One bad message must not stop the rest of the batch.
                    message.AttemptCount++;
                    message.LastError = ex.Message.Length > 2000
                        ? ex.Message[..2000]
                        : ex.Message;

                    if (message.AttemptCount >= MaxAttempts)
                    {
                        logger.LogError(ex,
                            "Outbox message {Id} ({EventType}) has failed {Attempts} times and will not be retried",
                            message.Id, message.EventType, message.AttemptCount);
                    }
                    else
                    {
                        logger.LogWarning(ex,
                            "Failed to publish outbox message {Id} ({EventType}), attempt {Attempts}",
                            message.Id, message.EventType, message.AttemptCount);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            if (published > 0)
                logger.LogInformation("Outbox published {Count} message(s)", published);

            return published;
        });
    }
}
