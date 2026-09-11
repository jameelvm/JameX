namespace JameX.Engagement.Configuration;

/// <summary>
/// Everything tunable about engagement, owned by this service alone — the same
/// reasoning as <c>JameX.Ingest.Configuration.UploadOptions</c>: no other
/// service has any business knowing these table names or shard counts.
/// </summary>
public sealed class EngagementOptions
{
    public const string SectionName = "Engagement";

    public string CountersTableName { get; set; } = "jamex-video-counters";
    public string ReactionsTableName { get; set; } = "jamex-user-reactions";

    /// <summary>
    /// How many shards a video's view counter is split across.
    /// <para>
    /// This is chapter 4's headline write-scaling problem made concrete. A
    /// single DynamoDB partition is capped at roughly 1,000 writes/sec; a
    /// viral video's view counter is the hottest key in the entire system and
    /// will blow through that on one item. Splitting the counter into N items
    /// (<c>VIEWS#0</c> … <c>VIEWS#9</c>) and picking one at random per write
    /// scatters the load — write throughput now scales with the shard count,
    /// at the cost of a read having to sum N items instead of reading one.
    /// </para>
    /// <para>
    /// Likes and dislikes are deliberately <b>not</b> sharded — see
    /// <c>UserReactionRepository</c>. A like requires a unique reaction row
    /// per user first, and that uniqueness check already caps the write rate
    /// far below what a raw, unauthenticated view ping can reach.
    /// </para>
    /// </summary>
    public int ViewShardCount { get; set; } = 10;
}
