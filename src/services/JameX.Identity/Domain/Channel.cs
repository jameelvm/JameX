namespace JameX.Identity.Domain;

/// <summary>
/// The publishing identity a video belongs to. Videos reference
/// <see cref="Id"/>, but Catalog stores that id as a plain column with no
/// foreign key — it physically cannot have one, because the channel lives in a
/// different database owned by a different service.
/// <para>
/// That is the trade the decomposition makes: the database can no longer refuse
/// a video whose channel does not exist, so the service has to. Catalog checks
/// with Identity at write time, and referential integrity becomes an
/// application concern instead of a constraint.
/// </para>
/// </summary>
public sealed class Channel
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid OwnerUserId { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// The <c>@handle</c>, stored lower-cased without the leading <c>@</c>.
    /// Globally unique because it appears in URLs, so it is a second identity
    /// for the channel and needs the same uniqueness guarantee as the id.
    /// </summary>
    public required string Handle { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Denormalised so a channel page costs one row read instead of counting a
    /// subscription table on every request.
    /// <para>
    /// Kept here only because subscriptions are low-volume. The same reasoning
    /// does <i>not</i> hold for view counts, which is why those live in DynamoDB
    /// as sharded counters — a single hot row cannot absorb the write rate a
    /// popular video generates (chapter 4).
    /// </para>
    /// </summary>
    public long SubscriberCount { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public User? Owner { get; init; }
}
