using JameX.Contracts.Dtos;
using JameX.Identity.Contracts;
using JameX.Identity.Domain;
using JameX.Identity.Mapping;
using JameX.Identity.Repositories;
using JameX.Identity.Validation;
using JameX.ServiceDefaults.Application;

namespace JameX.Identity.Services;

public interface IChannelService
{
    /// <param name="ownerUserId">
    /// Supplied by the endpoint from the authenticated caller, never from the
    /// request body — a client that can nominate the owner can create channels
    /// under someone else's account.
    /// </param>
    Task<OperationResult<ChannelDto>> CreateAsync(
        Guid ownerUserId, CreateChannelRequest request, CancellationToken ct);

    Task<OperationResult<ChannelDto>> GetAsync(Guid channelId, CancellationToken ct);
    Task<OperationResult<ChannelDto>> GetByHandleAsync(string handle, CancellationToken ct);
    Task<OperationResult<IReadOnlyList<ChannelDto>>> GetBatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
}

internal sealed class ChannelService(
    IChannelRepository channels,
    IUserRepository users) : IChannelService
{
    public async Task<OperationResult<ChannelDto>> CreateAsync(
        Guid ownerUserId, CreateChannelRequest request, CancellationToken ct)
    {
        var handle = Normalise.Handle(request.Handle);
        var name = request.Name.Trim();

        if (!Normalise.IsValidHandle(handle))
            return OperationResult<ChannelDto>.Invalid("handle",
                "3–30 characters, starting with a letter or digit, using only a–z, 0–9, dot, dash or underscore.");

        if (name.Length is < 1 or > 100)
            return OperationResult<ChannelDto>.Invalid(
                "name", "Channel name must be between 1 and 100 characters.");

        // Safe to check rather than rely on the foreign key, because the owner
        // lives in this service's own database. Catalog has no equivalent
        // option for ChannelId — that row is in a database it cannot reach.
        if (!await users.ExistsAsync(ownerUserId, ct))
            return OperationResult<ChannelDto>.NotFound("The calling user does not exist.");

        var channel = new Channel
        {
            OwnerUserId = ownerUserId,
            Name = name,
            Handle = handle,
            AvatarUrl = request.AvatarUrl
        };

        return await channels.TryAddAsync(channel, ct)
            ? OperationResult<ChannelDto>.Success(channel.ToDto())
            : OperationResult<ChannelDto>.Conflict($"The handle @{handle} is already taken.");
    }

    public async Task<OperationResult<ChannelDto>> GetAsync(Guid channelId, CancellationToken ct)
    {
        var channel = await channels.GetByIdAsync(channelId, ct);

        return channel is null
            ? OperationResult<ChannelDto>.NotFound()
            : OperationResult<ChannelDto>.Success(channel.ToDto());
    }

    /// <summary>
    /// Resolves a public <c>@handle</c> to a channel.
    /// <para>
    /// Handles are mutable, so nothing else in the system stores one as a
    /// reference — services speak <c>ChannelId</c> only, and a handle is
    /// translated exactly once, here, when a URL arrives.
    /// </para>
    /// </summary>
    public async Task<OperationResult<ChannelDto>> GetByHandleAsync(string handle, CancellationToken ct)
    {
        // Normalised identically to the write path, or the lookup misses a
        // channel that is plainly there.
        var channel = await channels.GetByHandleAsync(Normalise.Handle(handle), ct);

        return channel is null
            ? OperationResult<ChannelDto>.NotFound()
            : OperationResult<ChannelDto>.Success(channel.ToDto());
    }

    public async Task<OperationResult<IReadOnlyList<ChannelDto>>> GetBatchAsync(
        IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > Normalise.MaxBatchSize)
            return OperationResult<IReadOnlyList<ChannelDto>>.Invalid(
                "ids", $"A batch may contain at most {Normalise.MaxBatchSize} ids.");

        var found = await channels.GetManyAsync(ids, ct);

        return OperationResult<IReadOnlyList<ChannelDto>>.Success(
            found.Select(c => c.ToDto()).ToArray());
    }
}
