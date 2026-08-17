using JameX.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JameX.ServiceDefaults.Data;

/// <summary>
/// Applies an event exactly once, by writing the "I have handled this" record
/// and the change itself in a single database transaction.
/// <para>
/// The whole mechanism rests on one detail of EF Core: a single call to
/// <c>SaveChangesAsync</c> wraps <i>every</i> pending change in one transaction.
/// So claiming the event and applying its effects are not two steps that could
/// half-succeed — they are one atomic write. If the change commits, the claim
/// committed with it; if the claim loses a race, the change rolls back too.
/// </para>
/// <para>
/// This is why the inbox has to live in the same database as the data. Marking
/// an event handled in Redis, then crashing before the change commits, would
/// leave the event marked done and the work never performed — turning
/// "delivered twice" into "silently skipped", which is far worse.
/// </para>
/// </summary>
public interface IInboxUnitOfWork
{
    /// <summary>
    /// Stages the claim. Nothing is written until <see cref="TrySaveAsync"/>.
    /// </summary>
    void ClaimEvent(Guid eventId, string eventType, string source);

    /// <summary>
    /// Commits the claim and every other pending change together.
    /// <para>
    /// Returns <c>false</c> when this event has already been applied — the
    /// primary key on <c>processed_events</c> rejected the duplicate and the
    /// whole transaction, including the change, was rolled back. The caller
    /// should treat that as success and delete the message.
    /// </para>
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken ct);
}

public sealed class InboxUnitOfWork<TContext>(
    TContext db,
    ILogger<InboxUnitOfWork<TContext>> logger) : IInboxUnitOfWork
    where TContext : DbContext
{
    private Guid _claimedEventId;

    public void ClaimEvent(Guid eventId, string eventType, string source)
    {
        _claimedEventId = eventId;

        db.Set<ProcessedEvent>().Add(new ProcessedEvent
        {
            EventId = eventId,
            EventType = eventType,
            Source = source
        });
    }

    public async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            // One transaction covering the claim and every change staged
            // alongside it. Not two writes — one.
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.IsUniqueViolation(out var constraint)
                  && constraint?.Contains("processed_events", StringComparison.OrdinalIgnoreCase) == true)
        {
            logger.LogInformation(
                "Event {EventId} was already applied; skipping the redelivery", _claimedEventId);
            return false;
        }
    }
}

public static class InboxExtensions
{
    /// <summary>Claims an event straight from its envelope.</summary>
    public static void ClaimEvent<T>(this IInboxUnitOfWork inbox, EventEnvelope<T> envelope) =>
        inbox.ClaimEvent(envelope.EventId, envelope.EventType, envelope.Source);
}
