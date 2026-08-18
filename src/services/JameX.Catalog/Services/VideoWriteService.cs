using JameX.Catalog.Caching;
using JameX.Catalog.Contracts;
using JameX.Catalog.Domain;
using JameX.Catalog.Mapping;
using JameX.Catalog.Repositories;
using JameX.Contracts;
using JameX.Contracts.Dtos;
using JameX.Contracts.Events;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Configuration;
using JameX.ServiceDefaults.Data;
using Microsoft.Extensions.Options;

namespace JameX.Catalog.Services;

/// <summary>
/// The write half of Catalog: editing metadata and deleting videos.
/// <para>
/// Kept apart from <see cref="IVideoQueryService"/> because the two have almost
/// nothing in common. Reads are cached, anonymous and constant; writes are
/// authorised, rare, and have to publish events. Sharing one class would mean
/// one set of dependencies serving two completely different jobs.
/// </para>
/// </summary>
public interface IVideoWriteService
{
    Task<OperationResult<VideoDetail>> UpdateAsync(
        Guid videoId, Guid callerId, UpdateVideoRequest request, CancellationToken ct);

    Task<OperationResult<bool>> DeleteAsync(Guid videoId, Guid callerId, CancellationToken ct);
}

internal sealed class VideoWriteService(
    IVideoRepository videos,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IVideoCache cache,
    IOptions<StorageOptions> storageOptions,
    ILogger<VideoWriteService> logger) : IVideoWriteService
{
    private readonly StorageOptions _storage = storageOptions.Value;

    public async Task<OperationResult<VideoDetail>> UpdateAsync(
        Guid videoId, Guid callerId, UpdateVideoRequest request, CancellationToken ct)
    {
        var video = await videos.FindForUpdateAsync(videoId, ct);
        if (video is null) return OperationResult<VideoDetail>.NotFound();

        var authorised = Authorise(video, callerId);
        if (authorised is not null) return OperationResult<VideoDetail>.Forbidden(authorised);

        if (request.Title is { } title)
        {
            var trimmed = title.Trim();
            if (trimmed.Length is < 1 or > 200)
                return OperationResult<VideoDetail>.Invalid(
                    "title", "Title must be between 1 and 200 characters.");
            video.Title = trimmed;
        }

        // Null means "leave alone"; an explicit empty string clears the field.
        if (request.Description is not null)
            video.Description = request.Description.Length > 5000
                ? request.Description[..5000]
                : request.Description;

        if (request.CategoryId is not null) video.CategoryId = request.CategoryId;
        if (request.Tags is not null) video.Tags = request.Tags;

        if (request.Privacy is { } privacy)
        {
            video.Privacy = privacy;

            // Publishing is not just a flag. A video only enters the public
            // feed once it is both public AND playable, and published_at is
            // what the feed's partial index sorts on — so it has to be stamped
            // here as well as in the encode handler, whichever happens last.
            if (privacy == VideoPrivacy.Public
                && video.Status == VideoStatus.Ready
                && video.PublishedAt is null)
            {
                video.PublishedAt = DateTimeOffset.UtcNow;
            }
        }

        video.UpdatedAt = DateTimeOffset.UtcNow;

        await unitOfWork.SaveChangesAsync(ct);

        // After the commit, never before — a reader in the gap would repopulate
        // the cache from the old row.
        await cache.InvalidateAsync(videoId, ct);

        logger.LogInformation("Video {VideoId} updated by {CallerId}", videoId, callerId);

        // Re-read so the response includes the renditions, which the tracked
        // entity above was not asked to load.
        var updated = await videos.GetDetailAsync(videoId, ct);
        return OperationResult<VideoDetail>.Success(updated!.ToDetail(_storage));
    }

    /// <summary>
    /// Deletes the video and announces it — the transactional outbox in one
    /// method.
    /// </summary>
    public async Task<OperationResult<bool>> DeleteAsync(
        Guid videoId, Guid callerId, CancellationToken ct)
    {
        var video = await videos.FindForUpdateAsync(videoId, ct);
        if (video is null) return OperationResult<bool>.NotFound();

        var authorised = Authorise(video, callerId);
        if (authorised is not null) return OperationResult<bool>.Forbidden(authorised);

        var channelId = video.ChannelId;

        // Both staged, neither written yet.
        videos.Remove(video);
        outbox.Enqueue(EventTypes.VideoDeleted, new VideoDeleted(videoId, channelId, DateTimeOffset.UtcNow));

        // One transaction. The row disappears and the announcement becomes
        // durable together — so there is no crash point that deletes the video
        // while leaving Search and Engagement holding it forever.
        await unitOfWork.SaveChangesAsync(ct);

        await cache.InvalidateAsync(videoId, ct);

        logger.LogInformation(
            "Video {VideoId} deleted by {CallerId}; VideoDeleted queued in the outbox", videoId, callerId);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Returns an error message if the caller may not touch this video, or null
    /// if they may.
    /// <para>
    /// Catalog can only check the uploader, because <c>uploader_id</c> is a
    /// column it owns. It genuinely cannot verify "is this caller the channel's
    /// owner?" — that fact lives in Identity's database. In production the
    /// Gateway would resolve channel ownership once and forward it as a signed
    /// claim, so this service would not need a cross-service call on every
    /// write.
    /// </para>
    /// </summary>
    private static string? Authorise(Video video, Guid callerId) =>
        video.UploaderId == callerId
            ? null
            : "This video belongs to another uploader.";
}
