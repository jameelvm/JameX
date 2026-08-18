using JameX.Catalog.Contracts;
using JameX.Catalog.Services;
using JameX.Catalog.Validation;
using JameX.Contracts.Dtos;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Catalog.Api;

/// <summary>
/// Transport only: bind, call the service, translate the outcome. The one extra
/// job here is the cache header, which is an HTTP concern and so belongs at
/// this layer rather than inside the service.
/// </summary>
[ApiController]
[Route("videos")]
[Produces("application/json")]
public sealed class VideosController(
    IVideoQueryService videoQueryService,
    IVideoWriteService videoWriteService,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Header mirroring the nginx edge tier, so a cache hit is visible end to end.</summary>
    private const string CacheHeader = "X-JameX-Cache";

    /// <summary>
    /// The watch page. Served from Redis when warm — see
    /// <see cref="Caching.IVideoCache"/> for why this is cache-aside rather
    /// than write-through.
    /// </summary>
    [HttpGet("{videoId:guid}")]
    [ProducesResponseType<VideoDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid videoId, CancellationToken ct)
    {
        var result = await videoQueryService.GetAsync(videoId, ct);

        Response.Headers[CacheHeader] = videoQueryService.LastReadWasCacheHit ? "HIT" : "MISS";

        return result.ToActionResult();
    }

    /// <summary>
    /// The public feed: public and Ready only, newest published first.
    /// <para>
    /// Page size is clamped rather than rejected — an oversized request is a
    /// client bug, not an attack worth a 400, and silently capping it keeps one
    /// caller from asking for the whole catalogue in a single query.
    /// </para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<VideoSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeed(
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = CatalogRules.DefaultPageSize) =>
        (await videoQueryService.GetFeedAsync(page, pageSize, ct)).ToActionResult();

    /// <summary>A channel's published videos, newest first.</summary>
    [HttpGet("/channels/{channelId:guid}/videos")]
    [ProducesResponseType<PagedResult<VideoSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByChannel(
        Guid channelId,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = CatalogRules.DefaultPageSize) =>
        (await videoQueryService.GetByChannelAsync(channelId, page, pageSize, ct)).ToActionResult();

    /// <summary>
    /// Resolves many videos in one call, mirroring Identity's batch endpoints —
    /// the Gateway needs all three services to support this or the watch page
    /// degenerates into one request per item.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType<IReadOnlyList<VideoSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBatch(BatchLookupRequest request, CancellationToken ct) =>
        (await videoQueryService.GetBatchAsync(request.Ids, ct)).ToActionResult();

    /// <summary>
    /// Updates editable metadata. PATCH, not PUT — omitted fields are left
    /// alone rather than cleared.
    /// </summary>
    [HttpPatch("{videoId:guid}")]
    [ProducesResponseType<VideoDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid videoId, UpdateVideoRequest request, CancellationToken ct) =>
        (await videoWriteService.UpdateAsync(videoId, currentUser.RequireUserId(), request, ct))
            .ToActionResult();

    /// <summary>
    /// Deletes the video and queues <c>VideoDeleted</c> in the outbox, both in
    /// one transaction.
    /// </summary>
    [HttpDelete("{videoId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid videoId, CancellationToken ct) =>
        (await videoWriteService.DeleteAsync(videoId, currentUser.RequireUserId(), ct))
            .ToActionResult(_ => NoContent());
}
