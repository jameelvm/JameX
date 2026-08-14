namespace JameX.Identity.Domain;

/// <summary>
/// An account. Lives in <c>jamex_users</c>, which chapter 2 calls out as the one
/// store that must be <b>strongly consistent</b> — a user reading their own
/// profile immediately after changing it must see the change, and a duplicate
/// registration must be impossible rather than merely unlikely.
/// <para>
/// That is the whole reason this is a separate service from Catalog. Video
/// metadata is allowed to lag by seconds; account data is not. Splitting them
/// lets each store be tuned for its own consistency requirement instead of
/// forcing the strictest one on everything.
/// </para>
/// </summary>
public sealed class User
{
    /// <summary>
    /// UUID v7, not v4. Both are 128-bit and unique, but v7 embeds a timestamp
    /// in its high bits, so successive ids sort in creation order. As a primary
    /// key that means inserts land at the right-hand edge of the B-tree instead
    /// of scattering across every page — far fewer page splits and a far better
    /// cache hit rate on a table that only grows.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Stored already lower-cased. Uniqueness has to be case-insensitive
    /// (nobody accepts <c>Bob@x.com</c> and <c>bob@x.com</c> as two accounts),
    /// and normalising on write is what lets a plain unique index enforce that.
    /// A <c>lower(email)</c> functional index would work too, but then every
    /// lookup has to remember to call <c>lower()</c> or it silently misses the
    /// index.
    /// </summary>
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// A user may own several channels — the same split YouTube makes between
    /// a Google account and the channels under it.
    /// </summary>
    public ICollection<Channel> Channels { get; } = [];
}
