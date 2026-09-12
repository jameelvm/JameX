using JameX.Contracts.Dtos;
using JameX.Engagement.Contracts;
using JameX.Engagement.Services;
using JameX.Engagement.Validation;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Engagement.Api;

/// <summary>
/// Transport only — see <c>EngagementController</c>'s remark on the discipline
/// this repeats. Routes mirror the Gateway's <c>video-comments</c> route in
/// <c>JameX.Gateway/appsettings.json</c>.
/// </summary>
[ApiController]
[Route("videos/{videoId:guid}/comments")]
[Produces("application/json")]
public sealed class CommentsController(ICommentService comments, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Top-level comments for a video, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<CommentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPage(
        Guid videoId,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = EngagementRules.DefaultPageSize) =>
        (await comments.GetPageAsync(videoId, page, pageSize, ct)).ToActionResult();

    /// <summary>Replies to one top-level comment, oldest first.</summary>
    [HttpGet("{commentId:guid}/replies")]
    [ProducesResponseType<PagedResult<CommentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReplies(
        Guid commentId,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = EngagementRules.DefaultPageSize) =>
        (await comments.GetRepliesAsync(commentId, page, pageSize, ct)).ToActionResult();

    /// <summary>Posts a comment, or a reply when <c>parentCommentId</c> is set.</summary>
    [HttpPost]
    [ProducesResponseType<CommentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Add(
        Guid videoId, CreateCommentRequest request, CancellationToken ct) =>
        (await comments.AddAsync(videoId, currentUser.RequireUserId(), request, ct))
            .ToActionResult(dto => CreatedAtAction(nameof(GetPage), new { videoId }, dto));

    /// <summary>Edits the caller's own comment.</summary>
    [HttpPatch("{commentId:guid}")]
    [ProducesResponseType<CommentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid videoId, Guid commentId, UpdateCommentRequest request, CancellationToken ct) =>
        (await comments.UpdateAsync(commentId, currentUser.RequireUserId(), request, ct)).ToActionResult();

    /// <summary>
    /// Deletes the caller's own comment — a tombstone if it has replies, a
    /// physical delete otherwise. See <c>CommentService.DeleteAsync</c>.
    /// </summary>
    [HttpDelete("{commentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid videoId, Guid commentId, CancellationToken ct) =>
        (await comments.DeleteAsync(commentId, currentUser.RequireUserId(), ct))
            .ToActionResult(_ => NoContent());
}
