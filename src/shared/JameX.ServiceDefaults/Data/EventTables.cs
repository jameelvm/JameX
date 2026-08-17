using Microsoft.EntityFrameworkCore;

namespace JameX.ServiceDefaults.Data;

/// <summary>
/// Records an event this service has already applied.
/// <para>
/// This is the <b>inbox pattern</b>, and it is the durable answer to
/// at-least-once delivery. SNS-to-SQS will hand the same message over twice —
/// after a visibility-timeout expiry, a redrive, or a crash between doing the
/// work and deleting the message. A handler that increments a counter or
/// appends a row would then do it twice.
/// </para>
/// <para>
/// The Redis deduplicator in <c>Messaging</c> narrows that window but cannot
/// close it: it is a separate system, so "record that I handled this" and "apply
/// the change" can still fail independently. Writing this row <i>inside the same
/// database transaction</i> as the change makes the two atomic — either both
/// happened or neither did.
/// </para>
/// </summary>
public sealed class ProcessedEvent
{
    /// <summary>
    /// The <c>EventId</c> from the envelope, as primary key. The uniqueness
    /// constraint is the whole mechanism: a redelivery tries to insert a
    /// duplicate key and loses.
    /// </summary>
    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    /// <summary>Producing service, kept for tracing a replay after the fact.</summary>
    public required string Source { get; init; }

    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An event waiting to be published to SNS.
/// <para>
/// This is the <b>transactional outbox</b>, and it closes the dual-write hole.
/// A service that commits its database change and <i>then</i> publishes has two
/// writes to two systems with no transaction spanning them: crash in between and
/// the change is durable while the event is lost forever, leaving every
/// downstream service permanently wrong with nothing to detect it.
/// </para>
/// <para>
/// Writing the event to this table in the same transaction as the change makes
/// the decision to publish as durable as the change itself. A relay then reads
/// unpublished rows and sends them. That converts "might be lost" into "might be
/// sent twice" — which is exactly the problem <see cref="ProcessedEvent"/>
/// already solves on the consuming side.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// UUID v7 so the table is polled in insertion order without a sort, and
    /// inserts stay at the right-hand edge of the index.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>One of <c>JameX.Contracts.Events.EventTypes</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The serialised <c>EventEnvelope&lt;T&gt;</c>, stored as jsonb.</summary>
    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Null until the relay has successfully published it.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Publish attempts. A row that keeps failing needs to be visible rather
    /// than silently retried forever — this is what an alert would watch.
    /// </summary>
    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}

public static class EventTables
{
    /// <summary>
    /// Adds the inbox and outbox tables to a service's model.
    /// <para>
    /// Shared because every service that both consumes and publishes events
    /// needs exactly these two, and three different hand-rolled versions would
    /// be three different sets of bugs. They are infrastructure, not domain —
    /// which is why they live here rather than in a service's <c>Domain</c>.
    /// </para>
    /// </summary>
    public static ModelBuilder AddJameXEventTables(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedEvent>(processed =>
        {
            processed.ToTable("processed_events");
            processed.HasKey(p => p.EventId);

            processed.Property(p => p.EventType).HasMaxLength(100).IsRequired();
            processed.Property(p => p.Source).HasMaxLength(50).IsRequired();

            // Old rows are only useful for as long as a redelivery is possible.
            // A periodic delete by this index keeps the table from growing
            // without bound — SQS gives up after the retention period, so
            // anything older than that can never arrive again.
            processed.HasIndex(p => p.ProcessedAt).HasDatabaseName("ix_processed_events_processed_at");
        });

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(o => o.Id);

            outbox.Property(o => o.EventType).HasMaxLength(100).IsRequired();
            outbox.Property(o => o.Payload).HasColumnType("jsonb").IsRequired();
            outbox.Property(o => o.LastError).HasMaxLength(2000);

            // The relay's only query is "give me the unpublished ones, oldest
            // first". A partial index over just those rows stays small no matter
            // how large the table grows, because published rows — the vast
            // majority — are not in it at all.
            outbox.HasIndex(o => o.Id)
                .HasDatabaseName("ix_outbox_messages_unpublished")
                .HasFilter("published_at IS NULL");
        });

        return modelBuilder;
    }
}
