using JameX.Engagement.Data;
using JameX.Engagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace JameX.Engagement.Repositories;

/// <summary>
/// Data access for <c>comments</c>. No <c>SaveChangesAsync</c> here — see
/// Catalog's <c>IVideoRepository</c> for why staging and committing stay
/// separate; commits go through <see cref="ServiceDefaults.Data.IUnitOfWork"/>.
/// </summary>
public interface ICommentRepository
{
    /// <summary>Loaded tracked — the caller is about to edit or delete it.</summary>
    Task<Comment?> FindForUpdateAsync(Guid commentId, CancellationToken ct);

    /// <summary>Read-only lookup, for validating a reply's parent.</summary>
    Task<Comment?> FindAsync(Guid commentId, CancellationToken ct);

    Task<bool> HasRepliesAsync(Guid commentId, CancellationToken ct);

    /// <summary>
    /// Top-level comments only (<c>ParentCommentId IS NULL</c>), newest first —
    /// matches <c>ix_comments_video_id_created_at</c>. Replies are fetched
    /// separately, per parent, via <see cref="GetRepliesAsync"/>.
    /// </summary>
    Task<(IReadOnlyList<Comment> Items, int Total)> GetTopLevelPageAsync(
        Guid videoId, int page, int pageSize, CancellationToken ct);

    /// <summary>Replies to one top-level comment, oldest first — a reply thread reads as a conversation.</summary>
    Task<(IReadOnlyList<Comment> Items, int Total)> GetRepliesAsync(
        Guid parentCommentId, int page, int pageSize, CancellationToken ct);

    /// <summary>Every comment on a video, top-level and replies together — what <c>EngagementCounts.Comments</c> reports.</summary>
    Task<long> CountForVideoAsync(Guid videoId, CancellationToken ct);

    void Add(Comment comment);

    /// <summary>
    /// Physically removes the row. Only ever safe to call on a comment with no
    /// replies — see <see cref="Comment.IsDeleted"/> for the tombstone
    /// alternative used otherwise.
    /// </summary>
    void Remove(Comment comment);
}

internal sealed class CommentRepository(EngagementDbContext db) : ICommentRepository
{
    public Task<Comment?> FindForUpdateAsync(Guid commentId, CancellationToken ct) =>
        db.Comments.FirstOrDefaultAsync(c => c.Id == commentId, ct);

    public Task<Comment?> FindAsync(Guid commentId, CancellationToken ct) =>
        db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commentId, ct);

    public Task<bool> HasRepliesAsync(Guid commentId, CancellationToken ct) =>
        db.Comments.AsNoTracking().AnyAsync(c => c.ParentCommentId == commentId, ct);

    public async Task<(IReadOnlyList<Comment> Items, int Total)> GetTopLevelPageAsync(
        Guid videoId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Comments.AsNoTracking()
            .Where(c => c.VideoId == videoId && c.ParentCommentId == null);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<(IReadOnlyList<Comment> Items, int Total)> GetRepliesAsync(
        Guid parentCommentId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Comments.AsNoTracking()
            .Where(c => c.ParentCommentId == parentCommentId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public Task<long> CountForVideoAsync(Guid videoId, CancellationToken ct) =>
        db.Comments.AsNoTracking().LongCountAsync(c => c.VideoId == videoId, ct);

    public void Add(Comment comment) => db.Comments.Add(comment);

    public void Remove(Comment comment) => db.Comments.Remove(comment);
}
