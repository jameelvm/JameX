using JameX.Contracts;
using JameX.Contracts.Dtos;
using JameX.Engagement.Contracts;
using JameX.Engagement.Services;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Engagement.Api;

/// <summary>
/// Transport only: bind, call the service, translate the outcome — the same
/// discipline as Catalog's <c>VideosController</c>. Routes here mirror the
/// Gateway's YARP config in <c>JameX.Gateway/appsettings.json</c> exactly;
/// changing one without the other silently breaks routing.
/// </summary>
[ApiController]
[Route("videos/{videoId:guid}")]
[Produces("application/json")]
public sealed class EngagementController(
    IReactionService reactions,
    IViewService views,
    IEngagementQueryService query,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Views, likes and dislikes for a video.</summary>
    [HttpGet("counts")]
    [ProducesResponseType<EngagementCounts>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCounts(Guid videoId, CancellationToken ct) =>
        (await query.GetCountsAsync(videoId, ct)).ToActionResult();

    /// <summary>
    /// Records one view ping. Anonymous — a view carries no identity, unlike a
    /// reaction.
    /// </summary>
    [HttpPost("views")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RecordView(Guid videoId, CancellationToken ct)
    {
        await views.RecordViewAsync(videoId, ct);
        return Accepted();
    }

    /// <summary>The caller's own reaction, or null if they have not reacted.</summary>
    [HttpGet("reactions/me")]
    [ProducesResponseType<ReactionKind?>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyReaction(Guid videoId, CancellationToken ct) =>
        (await reactions.GetMyReactionAsync(videoId, currentUser.RequireUserId(), ct)).ToActionResult();

    /// <summary>
    /// Sets the caller's reaction — PUT because reacting is idempotent: calling
    /// this twice with the same kind leaves the video in the same state it was
    /// already in.
    /// </summary>
    [HttpPut("reactions/me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetMyReaction(
        Guid videoId, ReactionRequest request, CancellationToken ct) =>
        (await reactions.SetReactionAsync(videoId, currentUser.RequireUserId(), request.Kind, ct))
            .ToActionResult(_ => NoContent());

    /// <summary>Withdraws the caller's reaction, if any.</summary>
    [HttpDelete("reactions/me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemoveMyReaction(Guid videoId, CancellationToken ct) =>
        (await reactions.RemoveReactionAsync(videoId, currentUser.RequireUserId(), ct))
            .ToActionResult(_ => NoContent());
}
