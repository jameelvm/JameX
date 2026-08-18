using JameX.Contracts.Events;
using JameX.ServiceDefaults.Messaging;
using Microsoft.EntityFrameworkCore;

namespace JameX.ServiceDefaults.Data;

/// <summary>
/// Stages an event for publication as part of the current database
/// transaction.
/// <para>
/// Nothing is sent here. The event is written to <c>outbox_messages</c>
/// alongside the business change, and a relay publishes it afterwards. That is
/// the point: publishing directly would be a second write to a second system
/// with no transaction spanning the two, so a crash in between would lose the
/// event permanently while the change survived.
/// </para>
/// </summary>
public interface IOutbox
{
    /// <summary>
    /// Wraps <paramref name="data"/> in an envelope and stages it. Call
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> to commit it with the change.
    /// </summary>
    void Enqueue<T>(string eventType, T data);
}

public sealed class Outbox<TContext>(TContext db, IServiceIdentity identity) : IOutbox
    where TContext : DbContext
{
    public void Enqueue<T>(string eventType, T data)
    {
        // The envelope — and crucially its EventId — is created ONCE, here,
        // and stored. The relay publishes these exact bytes.
        //
        // If the relay built a fresh envelope on each attempt, a retry would
        // carry a new EventId, and every consumer's inbox would treat the
        // resend as a brand new event. The at-least-once guarantee would then
        // deliver the same change twice with nothing able to detect it. A
        // stable EventId is what makes redelivery safe.
        var envelope = new EventEnvelope<T>(
            EventId: Guid.CreateVersion7(),
            EventType: eventType,
            OccurredAt: DateTimeOffset.UtcNow,
            Source: identity.ServiceName,
            Data: data);

        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            EventType = eventType,
            Payload = JameXJson.Serialize(envelope)
        });
    }
}

/// <summary>
/// Commits everything staged in the current scope as one transaction.
/// <para>
/// Repositories deliberately do not save. Committing in one place is what lets
/// a business change and its outbox row — or its inbox claim — land atomically
/// rather than as two independent writes.
/// </para>
/// </summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed class UnitOfWork<TContext>(TContext db) : IUnitOfWork
    where TContext : DbContext
{
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
