namespace JameX.Contracts.Dtos;

/// <summary>
/// Compact projection for lists — search results, channel pages, home feed.
/// Kept deliberately small: a feed of 50 of these is fetched constantly, and it
/// is the shape the Redis cache stores.
/// </summary>
public sealed record VideoSummary(
    Guid VideoId,
    Guid ChannelId,
    string Title,
    double DurationSeconds,
    string? ThumbnailUrl,
    VideoStatus Status,
    DateTimeOffset? PublishedAt,
    long ViewCount,
    long LikeCount);

/// <summary>
/// The watch page. This is the aggregate the Gateway composes from Catalog,
/// Engagement and Identity — no single service owns all of it, which is the
/// point of the decomposition.
/// </summary>
public sealed record VideoDetail(
    Guid VideoId,
    Guid ChannelId,
    string? ChannelName,
    string Title,
    string? Description,
    string? CategoryId,
    string[] Tags,
    string DefaultLanguage,
    VideoPrivacy Privacy,
    VideoStatus Status,
    double DurationSeconds,
    string? MasterPlaylistUrl,
    string? PosterThumbnailUrl,
    IReadOnlyList<RenditionInfo> Renditions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    EngagementCounts Counts,
    ReactionKind? ViewerReaction);

/// <summary>
/// One rung of the ladder as exposed to clients. Clients do not normally need
/// this — hls.js reads the master playlist — but surfacing it makes adaptive
/// bitrate switching visible in the UI, which is the point of building it.
/// </summary>
public sealed record RenditionInfo(
    string Label,
    int Width,
    int Height,
    int BitrateKbps,
    string Codec,
    string PlaylistUrl);

public sealed record ThumbnailInfo(
    string ThumbnailId,
    string Url,
    int Width,
    int Height,
    double OffsetSeconds,
    bool IsPoster);

/// <summary>
/// Served from DynamoDB, not the relational store. View counts especially are
/// approximate by design — they are summed across counter shards, so a read
/// taken mid-write may trail reality by a few counts. Nobody notices, and it is
/// what lets writes scale past a single partition's throughput ceiling.
/// </summary>
public sealed record EngagementCounts(
    long Views,
    long Likes,
    long Dislikes,
    long Comments);

public sealed record CommentDto(
    Guid CommentId,
    Guid VideoId,
    Guid UserId,
    string? UserDisplayName,
    Guid? ParentCommentId,
    string Text,
    DateTimeOffset CreatedAt,
    bool IsEdited);

public sealed record SearchHit(
    Guid VideoId,
    string Title,
    string? ThumbnailUrl,
    double DurationSeconds,
    DateTimeOffset? PublishedAt,
    double Score,
    string MatchedOn);

public sealed record ChannelDto(
    Guid ChannelId,
    Guid OwnerUserId,
    string Name,
    string Handle,
    string? AvatarUrl,
    long SubscriberCount,
    DateTimeOffset CreatedAt);

public sealed record UserDto(
    Guid UserId,
    string Email,
    string DisplayName,
    DateTimeOffset CreatedAt);

/// <summary>Standard envelope for paged collections.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize)
{
    public bool HasMore => Page * PageSize < Total;
}
