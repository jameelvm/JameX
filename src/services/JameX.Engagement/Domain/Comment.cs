namespace JameX.Engagement.Domain;

/// <summary>
/// One comment. Lives in Postgres, not DynamoDB — comments are read as an
/// ordered, paginated list per video, and edited/deleted individually by id.
/// That is a relational access pattern, unlike the counters next to it in this
/// service, which are read by direct key lookup only.
/// </summary>
public sealed class Comment
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VideoId { get; init; }
    public required Guid UserId { get; init; }

    /// <summary>
    /// One level of nesting, not arbitrary threading. A reply's parent must
    /// itself be a top-level comment — enforced in the service layer, not the
    /// schema, because a self-referencing foreign key cannot express "at most
    /// one level deep" on its own.
    /// </summary>
    public Guid? ParentCommentId { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Derived from <c>CreatedAt != UpdatedAt</c> at the mapping layer, not
    /// stored — a second column that could drift from the timestamps it is
    /// summarising is a bug waiting to happen.
    /// </summary>
    public bool IsEdited => UpdatedAt > CreatedAt;

    // No UserDisplayName column, deliberately — Engagement never learned it in
    // the first place. A comment is created from a userId on the caller's
    // identity, not from anything Identity's database holds. The Gateway
    // overlays the display name at read time, the same way Catalog leaves
    // ChannelName null for the same reason.
}
