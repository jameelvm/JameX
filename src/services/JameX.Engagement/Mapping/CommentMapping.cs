using JameX.Contracts.Dtos;
using JameX.Engagement.Domain;

namespace JameX.Engagement.Mapping;

public static class CommentMapping
{
    private const string TombstoneText = "[deleted]";

    /// <summary>
    /// <c>UserDisplayName</c> is left null — see the remark on
    /// <see cref="Comment"/> for why Engagement never learns it — for the
    /// Gateway to overlay from Identity.
    /// </summary>
    public static CommentDto ToDto(this Comment comment) => new(
        comment.Id,
        comment.VideoId,
        comment.UserId,
        UserDisplayName: null,
        comment.ParentCommentId,
        comment.IsDeleted ? TombstoneText : comment.Text,
        comment.CreatedAt,
        comment.IsEdited);
}
