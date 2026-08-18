using JameX.Catalog.Caching;
using JameX.Catalog.Mapping;
using JameX.Catalog.Repositories;
using JameX.Catalog.Validation;
using JameX.Contracts;
using JameX.Contracts.Dtos;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace JameX.Catalog.Services;

/// <summary>
/// Every read path Catalog exposes. Separate from the write side because the
/// two have completely different shapes: reads are cached, unauthenticated and
/// enormously more frequent; writes are none of those things.
/// </summary>
public interface IVideoQueryService
{
    /// <summary>
    /// Whether the most recent <see cref="GetAsync"/> call was served from
    /// cache. Safe to expose as state because this service is <b>scoped</b> —
    /// one instance per request — and it lets the controller report the hit as
    /// a header without the service itself knowing anything about HTTP.
    /// </summary>
    bool LastReadWasCacheHit { get; }

    Task<OperationResult<VideoDetail>> GetAsync(Guid videoId, CancellationToken ct);
    Task<OperationResult<PagedResult<VideoSummary>>> GetFeedAsync(int page, int pageSize, CancellationToken ct);
    Task<OperationResult<PagedResult<VideoSummary>>> GetByChannelAsync(Guid channelId, int page, int pageSize, CancellationToken ct);
    Task<OperationResult<IReadOnlyList<VideoSummary>>> GetBatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
}

internal sealed class VideoQueryService(
    IVideoRepository videos,
    IVideoCache cache,
    IOptions<StorageOptions> storageOptions,
    ILogger<VideoQueryService> logger) : IVideoQueryService
{
    private readonly StorageOptions _storage = storageOptions.Value;

    /// <summary>
    /// The canonical cache-aside read, in four steps.
    /// </summary>
    public async Task<OperationResult<VideoDetail>> GetAsync(Guid videoId, CancellationToken ct)
    {
        // 1. Ask the cache.
        var cached = await cache.GetAsync(videoId, ct);
        if (cached is not null)
        {
            LastReadWasCacheHit = true;
            return OperationResult<VideoDetail>.Success(cached);
        }

        LastReadWasCacheHit = false;

        // 2. Miss — go to the system of record.
        var video = await videos.GetDetailAsync(videoId, ct);
        if (video is null) return OperationResult<VideoDetail>.NotFound();

        var detail = video.ToDetail(_storage);

        // 3. Populate, but only what is worth caching. A video still encoding
        //    changes again within minutes, and caching it just guarantees a
        //    stale watch page for someone. Only settled rows are stored.
        if (video.Status == VideoStatus.Ready)
            await cache.SetAsync(detail, ct);
        else
            logger.LogDebug("Not caching video {VideoId} in status {Status}", videoId, video.Status);

        // 4. Return the freshly read value either way.
        return OperationResult<VideoDetail>.Success(detail);
    }

    public bool LastReadWasCacheHit { get; private set; }

    /// <summary>
    /// The public feed. Deliberately <b>not</b> cached here.
    /// <para>
    /// A feed page has no precise invalidation key: publishing a single video
    /// changes the contents of every page after it, so a correct invalidation
    /// would mean dropping the whole feed on every publish. Feeds are better
    /// served by a short TTL at the edge, where staleness is measured in
    /// seconds and nobody is harmed by it — which is what the nginx tier is for.
    /// </para>
    /// </summary>
    public async Task<OperationResult<PagedResult<VideoSummary>>> GetFeedAsync(
        int page, int pageSize, CancellationToken ct)
    {
        page = CatalogRules.NormalisePage(page);
        pageSize = CatalogRules.NormalisePageSize(pageSize);

        var (items, total) = await videos.GetPublicFeedAsync(page, pageSize, ct);

        return OperationResult<PagedResult<VideoSummary>>.Success(
            new PagedResult<VideoSummary>(
                items.Select(v => v.ToSummary(_storage)).ToArray(), total, page, pageSize));
    }

    public async Task<OperationResult<PagedResult<VideoSummary>>> GetByChannelAsync(
        Guid channelId, int page, int pageSize, CancellationToken ct)
    {
        page = CatalogRules.NormalisePage(page);
        pageSize = CatalogRules.NormalisePageSize(pageSize);

        var (items, total) = await videos.GetByChannelAsync(channelId, page, pageSize, ct);

        // No 404 for an unknown channel: Catalog cannot tell "no such channel"
        // from "a real channel with nothing published", because channels live
        // in Identity. Claiming a 404 would be asserting something this service
        // has no way to know.
        return OperationResult<PagedResult<VideoSummary>>.Success(
            new PagedResult<VideoSummary>(
                items.Select(v => v.ToSummary(_storage)).ToArray(), total, page, pageSize));
    }

    public async Task<OperationResult<IReadOnlyList<VideoSummary>>> GetBatchAsync(
        IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > CatalogRules.MaxBatchSize)
            return OperationResult<IReadOnlyList<VideoSummary>>.Invalid(
                "ids", $"A batch may contain at most {CatalogRules.MaxBatchSize} ids.");

        var found = await videos.GetManyAsync(ids, ct);

        return OperationResult<IReadOnlyList<VideoSummary>>.Success(
            found.Select(v => v.ToSummary(_storage)).ToArray());
    }
}
