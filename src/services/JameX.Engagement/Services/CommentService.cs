using JameX.Contracts.Dtos;
using JameX.Engagement.Contracts;
using JameX.Engagement.Domain;
using JameX.Engagement.Mapping;
using JameX.Engagement.Repositories;
using JameX.Engagement.Validation;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Data;

namespace JameX.Engagement.Services;

/// <summary>
/// Comments: post, list, edit, delete. The one piece of Engagement backed
/// entirely by Postgres — see <see cref="Domain.Comment"/> for why.
/// </summary>
public interface ICommentService
{
    Task<OperationResult<PagedResult<CommentDto>>> GetPageAsync(
        Guid videoId, int page, int pageSize, CancellationToken ct);

    Task<OperationResult<PagedResult<CommentDto>>> GetRepliesAsync(
        Guid parentCommentId, int page, int pageSize, CancellationToken ct);

    Task<OperationResult<CommentDto>> AddAsync(
        Guid videoId, Guid userId, CreateCommentRequest request, CancellationToken ct);

    Task<OperationResult<CommentDto>> UpdateAsync(
        Guid commentId, Guid callerId, UpdateCommentRequest request, CancellationToken ct);

    Task<OperationResult<bool>> DeleteAsync(Guid commentId, Guid callerId, CancellationToken ct);
}

internal sealed class CommentService(
    ICommentRepository comments,
    IUnitOfWork unitOfWork,
    ILogger<CommentService> logger) : ICommentService
{
    public async Task<OperationResult<PagedResult<CommentDto>>> GetPageAsync(
        Guid videoId, int page, int pageSize, CancellationToken ct)
    {
        page = EngagementRules.NormalisePage(page);
        pageSize = EngagementRules.NormalisePageSize(pageSize);

        var (items, total) = await comments.GetTopLevelPageAsync(videoId, page, pageSize, ct);

        return OperationResult<PagedResult<CommentDto>>.Success(
            new PagedResult<CommentDto>(items.Select(c => c.ToDto()).ToArray(), total, page, pageSize));
    }

    public async Task<OperationResult<PagedResult<CommentDto>>> GetRepliesAsync(
        Guid parentCommentId, int page, int pageSize, CancellationToken ct)
    {
        page = EngagementRules.NormalisePage(page);
        pageSize = EngagementRules.NormalisePageSize(pageSize);

        var (items, total) = await comments.GetRepliesAsync(parentCommentId, page, pageSize, ct);

        return OperationResult<PagedResult<CommentDto>>.Success(
            new PagedResult<CommentDto>(items.Select(c => c.ToDto()).ToArray(), total, page, pageSize));
    }

    public async Task<OperationResult<CommentDto>> AddAsync(
        Guid videoId, Guid userId, CreateCommentRequest request, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        if (text.Length is < 1 or > EngagementRules.MaxCommentLength)
            return OperationResult<CommentDto>.Invalid(
                "text", $"Text must be between 1 and {EngagementRules.MaxCommentLength} characters.");

        if (request.ParentCommentId is { } parentId)
        {
            var parent = await comments.FindAsync(parentId, ct);

            if (parent is null || parent.VideoId != videoId)
                return OperationResult<CommentDto>.Invalid(
                    "parentCommentId", "The parent comment does not exist on this video.");

            // One level of nesting, not arbitrary threading — see the remark
            // on Comment.ParentCommentId. A reply's parent must itself be a
            // top-level comment.
            if (parent.ParentCommentId is not null)
                return OperationResult<CommentDto>.Invalid(
                    "parentCommentId", "Replies cannot themselves be replied to.");
        }

        var comment = new Comment
        {
            VideoId = videoId,
            UserId = userId,
            ParentCommentId = request.ParentCommentId,
            Text = text
        };

        comments.Add(comment);
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation(
            "User {UserId} commented on video {VideoId} (comment {CommentId}, parent {ParentId})",
            userId, videoId, comment.Id, request.ParentCommentId?.ToString() ?? "none");

        return OperationResult<CommentDto>.Success(comment.ToDto());
    }

    public async Task<OperationResult<CommentDto>> UpdateAsync(
        Guid commentId, Guid callerId, UpdateCommentRequest request, CancellationToken ct)
    {
        var comment = await comments.FindForUpdateAsync(commentId, ct);
        if (comment is null) return OperationResult<CommentDto>.NotFound();

        if (comment.UserId != callerId)
            return OperationResult<CommentDto>.Forbidden("This comment belongs to another user.");

        if (comment.IsDeleted)
            return OperationResult<CommentDto>.Invalid("text", "A deleted comment cannot be edited.");

        var text = request.Text?.Trim() ?? "";
        if (text.Length is < 1 or > EngagementRules.MaxCommentLength)
            return OperationResult<CommentDto>.Invalid(
                "text", $"Text must be between 1 and {EngagementRules.MaxCommentLength} characters.");

        comment.Text = text;
        comment.UpdatedAt = DateTimeOffset.UtcNow;

        await unitOfWork.SaveChangesAsync(ct);

        return OperationResult<CommentDto>.Success(comment.ToDto());
    }

    /// <summary>
    /// Removes a comment with no replies outright; tombstones one that has
    /// replies instead of leaving them pointing at nothing. See
    /// <see cref="Comment.IsDeleted"/> for why the schema is built this way.
    /// </summary>
    public async Task<OperationResult<bool>> DeleteAsync(Guid commentId, Guid callerId, CancellationToken ct)
    {
        var comment = await comments.FindForUpdateAsync(commentId, ct);
        if (comment is null) return OperationResult<bool>.NotFound();

        if (comment.UserId != callerId)
            return OperationResult<bool>.Forbidden("This comment belongs to another user.");

        if (await comments.HasRepliesAsync(commentId, ct))
        {
            comment.IsDeleted = true;
            comment.Text = "";
            comment.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            comments.Remove(comment);
        }

        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} deleted comment {CommentId}", callerId, commentId);

        return OperationResult<bool>.Success(true);
    }
}
