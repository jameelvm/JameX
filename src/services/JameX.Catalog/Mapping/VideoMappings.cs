using JameX.Catalog.Domain;
using JameX.Contracts.Dtos;
using JameX.ServiceDefaults.Configuration;

namespace JameX.Catalog.Mapping;

/// <summary>
/// Entity to DTO, plus the one piece of real logic in this layer: turning
/// storage keys into client-facing URLs.
/// <para>
/// The database stores <c>videos/abc/master.m3u8</c> — an object key, not a
/// URL. Which host serves it is a deployment decision, so it is applied here
/// from <see cref="StorageOptions.CdnBaseUrl"/> rather than baked into a
/// column. Moving from the local nginx cache to a real CloudFront distribution
/// is then a config change, and no stored row has to be rewritten.
/// </para>
/// </summary>
public static class VideoMappings
{
    public static VideoSummary ToSummary(this Video video, StorageOptions storage) =>
        new(video.Id,
            video.ChannelId,
            video.Title,
            video.DurationSeconds ?? 0,
            video.PosterThumbnailKey is null ? null : storage.ToCdnUrl(video.PosterThumbnailKey),
            video.Status,
            video.PublishedAt,
            // Views and likes belong to Engagement, which owns its own store.
            // Catalog returns zeros and the Gateway overlays the real numbers —
            // guessing them here would mean reading another service's data.
            ViewCount: 0,
            LikeCount: 0);

    /// <summary>
    /// The watch page as far as Catalog can see it.
    /// <para>
    /// <see cref="VideoDetail.ChannelName"/>, <see cref="VideoDetail.Counts"/>
    /// and <see cref="VideoDetail.ViewerReaction"/> are left empty on purpose.
    /// They live in Identity and Engagement, and Catalog cannot read another
    /// service's database. The Gateway fills them in — that fan-out is exactly
    /// why the batch endpoints exist.
    /// </para>
    /// </summary>
    public static VideoDetail ToDetail(this Video video, StorageOptions storage) =>
        new(video.Id,
            video.ChannelId,
            ChannelName: null,
            video.Title,
            video.Description,
            video.CategoryId,
            video.Tags,
            video.DefaultLanguage,
            video.Privacy,
            video.Status,
            video.DurationSeconds ?? 0,
            video.MasterPlaylistKey is null ? null : storage.ToCdnUrl(video.MasterPlaylistKey),
            video.PosterThumbnailKey is null ? null : storage.ToCdnUrl(video.PosterThumbnailKey),
            video.Renditions
                .OrderBy(r => r.BitrateKbps)
                .Select(r => r.ToInfo(storage))
                .ToArray(),
            video.CreatedAt,
            video.PublishedAt,
            Counts: new EngagementCounts(0, 0, 0, 0),
            ViewerReaction: null);

    private static RenditionInfo ToInfo(this Rendition rendition, StorageOptions storage) =>
        new(rendition.Label,
            rendition.Width,
            rendition.Height,
            rendition.BitrateKbps,
            rendition.Codec,
            storage.ToCdnUrl(rendition.PlaylistKey));
}
