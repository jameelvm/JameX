using JameX.Contracts;
using JameX.Engagement.Repositories;
using JameX.ServiceDefaults.Application;

namespace JameX.Engagement.Services;

/// <summary>
/// Likes, dislikes, and switching between them.
/// <para>
/// One row per (user, video) in <see cref="IUserReactionRepository"/> is the
/// source of truth for "what did this user react with"; the counters in
/// <see cref="IVideoCounterRepository"/> are a derived total kept in step with
/// it. Every method here exists to keep those two stores consistent with each
/// other despite living in different DynamoDB tables with no way to write to
/// both atomically.
/// </para>
/// </summary>
public interface IReactionService
{
    Task<OperationResult<ReactionKind?>> GetMyReactionAsync(Guid videoId, Guid userId, CancellationToken ct);

    Task<OperationResult<bool>> SetReactionAsync(
        Guid videoId, Guid userId, ReactionKind kind, CancellationToken ct);

    Task<OperationResult<bool>> RemoveReactionAsync(Guid videoId, Guid userId, CancellationToken ct);
}

internal sealed class ReactionService(
    IUserReactionRepository reactions,
    IVideoCounterRepository counters,
    ILogger<ReactionService> logger) : IReactionService
{
    public async Task<OperationResult<ReactionKind?>> GetMyReactionAsync(
        Guid videoId, Guid userId, CancellationToken ct) =>
        OperationResult<ReactionKind?>.Success(await reactions.GetAsync(userId, videoId, ct));

    /// <summary>
    /// Sets, switches, or reaffirms a reaction.
    /// <para>
    /// The whole method hinges on <see cref="IUserReactionRepository.PutAsync"/>
    /// atomically reporting what it replaced. That is what makes it safe to
    /// decide the counter adjustment <i>after</i> the write instead of before —
    /// a plain read-then-write would let two concurrent clicks from the same
    /// user both see "no reaction" and both increment the counter.
    /// </para>
    /// </summary>
    public async Task<OperationResult<bool>> SetReactionAsync(
        Guid videoId, Guid userId, ReactionKind kind, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind))
            return OperationResult<bool>.Invalid("kind", $"'{kind}' is not a recognised reaction.");

        var previous = await reactions.PutAsync(userId, videoId, kind, ct);

        // Reaffirming the same reaction is a no-op — the counters already
        // reflect it from whenever it was first set.
        if (previous == kind)
            return OperationResult<bool>.Success(true);

        var adjustments = new List<Task>(2);

        if (previous is { } old)
            adjustments.Add(Adjust(videoId, old, -1, ct));

        adjustments.Add(Adjust(videoId, kind, 1, ct));

        await Task.WhenAll(adjustments);

        logger.LogInformation(
            "User {UserId} set reaction {Kind} on video {VideoId} (was {Previous})",
            userId, kind, videoId, previous?.ToString() ?? "none");

        return OperationResult<bool>.Success(true);
    }

    public async Task<OperationResult<bool>> RemoveReactionAsync(
        Guid videoId, Guid userId, CancellationToken ct)
    {
        var previous = await reactions.RemoveAsync(userId, videoId, ct);

        if (previous is not { } kind)
            return OperationResult<bool>.Success(true);

        await Adjust(videoId, kind, -1, ct);

        logger.LogInformation(
            "User {UserId} removed their {Kind} reaction on video {VideoId}", userId, kind, videoId);

        return OperationResult<bool>.Success(true);
    }

    private Task Adjust(Guid videoId, ReactionKind kind, int delta, CancellationToken ct) =>
        kind == ReactionKind.Like
            ? counters.AdjustLikesAsync(videoId, delta, ct)
            : counters.AdjustDislikesAsync(videoId, delta, ct);
}
