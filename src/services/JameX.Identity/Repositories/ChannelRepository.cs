using JameX.Identity.Data;
using JameX.Identity.Domain;
using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;

namespace JameX.Identity.Repositories;

/// <summary>
/// Every way this service reaches <c>channels</c>. Each method corresponds to an
/// index on the table — by primary key, by the unique handle, and by owner.
/// </summary>
public interface IChannelRepository
{
    Task<Channel?> GetByIdAsync(Guid channelId, CancellationToken ct);

    /// <summary><paramref name="normalisedHandle"/> must already be lower-cased and stripped of '@'.</summary>
    Task<Channel?> GetByHandleAsync(string normalisedHandle, CancellationToken ct);

    Task<IReadOnlyList<Channel>> GetManyAsync(IReadOnlyCollection<Guid> channelIds, CancellationToken ct);

    Task<IReadOnlyList<Channel>> GetByOwnerAsync(Guid ownerUserId, CancellationToken ct);

    /// <summary>Inserts the channel, or reports that the handle is taken.</summary>
    Task<bool> TryAddAsync(Channel channel, CancellationToken ct);
}

internal sealed class ChannelRepository(IdentityDbContext db) : IChannelRepository
{
    public Task<Channel?> GetByIdAsync(Guid channelId, CancellationToken ct) =>
        db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, ct);

    public Task<Channel?> GetByHandleAsync(string normalisedHandle, CancellationToken ct) =>
        db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Handle == normalisedHandle, ct);

    public async Task<IReadOnlyList<Channel>> GetManyAsync(
        IReadOnlyCollection<Guid> channelIds, CancellationToken ct)
    {
        if (channelIds.Count == 0) return [];

        var ids = channelIds.Distinct().ToArray();

        return await db.Channels.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);
    }

    // The only non-key access path, and the reason ix_channels_owner_user_id exists.
    public async Task<IReadOnlyList<Channel>> GetByOwnerAsync(Guid ownerUserId, CancellationToken ct) =>
        await db.Channels.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<bool> TryAddAsync(Channel channel, CancellationToken ct)
    {
        db.Channels.Add(channel);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(out _))
        {
            db.Entry(channel).State = EntityState.Detached;
            return false;
        }
    }
}
