using JameX.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace JameX.ServiceDefaults.Messaging;

/// <summary>
/// Handles one event type. A service registers one implementation per event
/// type it subscribes to; the consumer loop dispatches on
/// <see cref="EventType"/>.
/// </summary>
public interface IEventHandler
{
    /// <summary>One of <see cref="EventTypes"/>.</summary>
    string EventType { get; }

    /// <summary>
    /// Applies the event.
    /// <para>
    /// <b>Must be idempotent.</b> SNS-to-SQS delivery is at-least-once, and a
    /// handler that runs longer than the visibility timeout will be delivered
    /// to a second consumer while the first is still working. Throwing puts the
    /// message back for retry; after the queue's <c>maxReceiveCount</c> it is
    /// redriven to the DLQ.
    /// </para>
    /// </summary>
    Task HandleAsync(string rawEnvelopeJson, CancellationToken ct);
}

/// <summary>
/// Typed base class that deals with deserialization so handlers only implement
/// business logic.
/// </summary>
public abstract class EventHandlerBase<TPayload> : IEventHandler
{
    protected EventHandlerBase(ILogger logger) => Logger = logger;

    protected ILogger Logger { get; }

    public abstract string EventType { get; }

    public async Task HandleAsync(string rawEnvelopeJson, CancellationToken ct)
    {
        var envelope = JameXJson.Deserialize<EventEnvelope<TPayload>>(rawEnvelopeJson)
            ?? throw new InvalidOperationException(
                $"Could not deserialize a {EventType} envelope. Body: {Truncate(rawEnvelopeJson)}");

        Logger.LogInformation(
            "Handling {EventType} {EventId} from {Source} (raised {OccurredAt:O})",
            envelope.EventType, envelope.EventId, envelope.Source, envelope.OccurredAt);

        await HandleAsync(envelope, ct);
    }

    protected abstract Task HandleAsync(EventEnvelope<TPayload> envelope, CancellationToken ct);

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}

/// <summary>
/// Guards against re-applying an event that has already been processed.
/// <para>
/// The durable form of this is the <i>inbox pattern</i>: record the event id in
/// the same database transaction that applies the change, so the check and the
/// write commit together. Services owning a relational store should do exactly
/// that. The Redis implementation here is a best-effort filter for services
/// with no relational store of their own — it narrows the window but cannot
/// close it, which is why handlers still have to be genuinely idempotent.
/// </para>
/// </summary>
public interface IEventDeduplicator
{
    /// <summary>
    /// Returns true if this event id has not been seen before, and records it.
    /// Returns false if it is a duplicate that should be skipped.
    /// </summary>
    Task<bool> TryBeginAsync(Guid eventId, CancellationToken ct = default);
}
