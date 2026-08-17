using JameX.Contracts.Dtos;
using JameX.Identity.Contracts;
using JameX.Identity.Domain;
using JameX.Identity.Mapping;
using JameX.Identity.Repositories;
using JameX.Identity.Validation;
using JameX.ServiceDefaults.Application;

namespace JameX.Identity.Services;

/// <summary>
/// Application logic for accounts: what a valid registration is, what a
/// duplicate means, and what shape the answer takes.
/// <para>
/// Nothing here references <c>HttpContext</c>, <c>IResult</c> or a status code.
/// That is deliberate — the same methods have to be callable from an event
/// handler or a test, and a service that returns <c>Results.Conflict()</c> has
/// quietly become an HTTP endpoint with extra steps.
/// </para>
/// </summary>
public interface IUserService
{
    Task<OperationResult<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken ct);
    Task<OperationResult<UserDto>> GetAsync(Guid userId, CancellationToken ct);
    Task<OperationResult<IReadOnlyList<UserDto>>> GetBatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<OperationResult<IReadOnlyList<ChannelDto>>> GetChannelsAsync(Guid userId, CancellationToken ct);
}

internal sealed class UserService(
    IUserRepository users,
    IChannelRepository channels) : IUserService
{
    public async Task<OperationResult<UserDto>> CreateAsync(
        CreateUserRequest request, CancellationToken ct)
    {
        var email = Normalise.Email(request.Email);
        var displayName = request.DisplayName.Trim();

        if (!Normalise.IsPlausibleEmail(email))
            return OperationResult<UserDto>.Invalid("email", "A valid email address is required.");

        if (displayName.Length is < 1 or > 100)
            return OperationResult<UserDto>.Invalid(
                "displayName", "Display name must be between 1 and 100 characters.");

        var user = new User { Email = email, DisplayName = displayName };

        // No "does this email exist?" pre-check. Two concurrent registrations
        // would both read absent and both insert; only the unique index
        // actually prevents the duplicate, so the index is what we ask.
        return await users.TryAddAsync(user, ct)
            ? OperationResult<UserDto>.Success(user.ToDto())
            : OperationResult<UserDto>.Conflict("That email address is already registered.");
    }

    public async Task<OperationResult<UserDto>> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(userId, ct);

        return user is null
            ? OperationResult<UserDto>.NotFound()
            : OperationResult<UserDto>.Success(user.ToDto());
    }

    /// <summary>
    /// Ids that do not exist are absent from the result rather than an error: a
    /// feed of fifty videos should still render when one uploader has deleted
    /// their account.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<UserDto>>> GetBatchAsync(
        IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > Normalise.MaxBatchSize)
            return OperationResult<IReadOnlyList<UserDto>>.Invalid(
                "ids", $"A batch may contain at most {Normalise.MaxBatchSize} ids.");

        var found = await users.GetManyAsync(ids, ct);

        return OperationResult<IReadOnlyList<UserDto>>.Success(
            found.Select(u => u.ToDto()).ToArray());
    }

    public async Task<OperationResult<IReadOnlyList<ChannelDto>>> GetChannelsAsync(
        Guid userId, CancellationToken ct)
    {
        // Distinguishes "no such user" from "user with no channels" — the first
        // is a 404, the second an empty list, and collapsing them hides a
        // genuine client error.
        if (!await users.ExistsAsync(userId, ct))
            return OperationResult<IReadOnlyList<ChannelDto>>.NotFound();

        var owned = await channels.GetByOwnerAsync(userId, ct);

        return OperationResult<IReadOnlyList<ChannelDto>>.Success(
            owned.Select(c => c.ToDto()).ToArray());
    }
}
