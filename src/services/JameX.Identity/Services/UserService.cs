using JameX.Contracts.Dtos;
using JameX.Identity.Contracts;
using JameX.Identity.Domain;
using JameX.Identity.Mapping;
using JameX.Identity.Repositories;
using JameX.Identity.Validation;
using JameX.ServiceDefaults.Application;
using Microsoft.AspNetCore.Identity;

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
    Task<OperationResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<OperationResult<UserDto>> GetAsync(Guid userId, CancellationToken ct);
    Task<OperationResult<IReadOnlyList<UserDto>>> GetBatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<OperationResult<IReadOnlyList<ChannelDto>>> GetChannelsAsync(Guid userId, CancellationToken ct);
}

internal sealed class UserService(
    IUserRepository users,
    IChannelRepository channels,
    IPasswordHasher<User> passwordHasher,
    ITokenService tokenService) : IUserService
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

        if (!Normalise.IsValidPassword(request.Password))
            return OperationResult<UserDto>.Invalid(
                "password", "Password must be at least 8 characters.");

        // PasswordHasher needs a User instance to hash against (its interface
        // is generic over TUser, in case a real ASP.NET Identity store wanted
        // per-user hashing parameters), but never reads anything off it — the
        // hash itself is what actually goes on the entity we save.
        var user = new User { Email = email, DisplayName = displayName, PasswordHash = "" };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        // No "does this email exist?" pre-check. Two concurrent registrations
        // would both read absent and both insert; only the unique index
        // actually prevents the duplicate, so the index is what we ask.
        return await users.TryAddAsync(user, ct)
            ? OperationResult<UserDto>.Success(user.ToDto())
            : OperationResult<UserDto>.Conflict("That email address is already registered.");
    }

    /// <summary>
    /// One deliberately vague failure message for both "no such account" and
    /// "wrong password" — telling an attacker which one narrows their search
    /// space for free, and a real user gets the same actionable advice
    /// ("check your email and password") either way.
    /// </summary>
    public async Task<OperationResult<AuthResponse>> LoginAsync(
        LoginRequest request, CancellationToken ct)
    {
        var email = Normalise.Email(request.Email);
        var user = await users.GetByEmailAsync(email, ct);

        if (user is null)
        {
            // Still runs a hash verification against a throwaway value even
            // though there is no user to check against — a real lookup and a
            // "no such email" rejection should take about the same amount of
            // time, or the response latency itself leaks which emails exist.
            passwordHasher.HashPassword(new User { Email = email, DisplayName = "", PasswordHash = "" }, request.Password);
            return OperationResult<AuthResponse>.Unauthorized("Invalid email or password.");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
            return OperationResult<AuthResponse>.Unauthorized("Invalid email or password.");

        var token = tokenService.IssueToken(user.Id, user.Email);
        return OperationResult<AuthResponse>.Success(new AuthResponse(token, user.ToDto()));
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
