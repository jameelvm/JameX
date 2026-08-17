using JameX.Contracts.Dtos;
using JameX.Identity.Contracts;
using JameX.Identity.Services;
using JameX.ServiceDefaults.Application;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Identity.Api;

/// <summary>
/// Transport only: bind the request, call the service, translate the outcome to
/// a status code. No validation, no queries, no business rules — if an action
/// here grows an <c>if</c>, the rule it encodes belongs in
/// <see cref="IUserService"/>.
/// </summary>
[ApiController]
[Route("users")]
[Produces("application/json")]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    /// <summary>Registers an account.</summary>
    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct) =>
        (await userService.CreateAsync(request, ct))
            // 201 rather than the default 200, so the Location header carries
            // the id the client did not choose.
            .ToActionResult(user => Created($"/users/{user.UserId}", user));

    [HttpGet("{userId:guid}")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct) =>
        (await userService.GetAsync(userId, ct)).ToActionResult();

    /// <summary>
    /// Resolves many users in one call — the endpoint the Gateway needs.
    /// <para>
    /// A feed of fifty videos carries fifty uploader ids. Without this the
    /// Gateway makes fifty HTTP calls: the N+1 problem, except each "+1" is a
    /// network round trip with its own latency and failure mode.
    /// </para>
    /// <para>
    /// POST because a hundred ids do not fit comfortably in a query string —
    /// the request is still a read despite the verb.
    /// </para>
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType<IReadOnlyList<UserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBatch(BatchLookupRequest request, CancellationToken ct) =>
        (await userService.GetBatchAsync(request.Ids, ct)).ToActionResult();

    [HttpGet("{userId:guid}/channels")]
    [ProducesResponseType<IReadOnlyList<ChannelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChannels(Guid userId, CancellationToken ct) =>
        (await userService.GetChannelsAsync(userId, ct)).ToActionResult();
}
