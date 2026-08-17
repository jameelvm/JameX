using JameX.Contracts.Dtos;
using JameX.Identity.Contracts;
using JameX.Identity.Services;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Identity.Api;

[ApiController]
[Route("channels")]
[Produces("application/json")]
public sealed class ChannelsController(
    IChannelService channelService,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Creates a channel owned by the calling user.
    /// <para>
    /// Reading the caller's identity is the one job that genuinely belongs at
    /// this layer — it is a property of the transport, not of the domain. The
    /// service is handed an owner id and never sees a request header, which
    /// keeps it callable from a seeding script or a test.
    /// </para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ChannelDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateChannelRequest request, CancellationToken ct) =>
        (await channelService.CreateAsync(currentUser.RequireUserId(), request, ct))
            .ToActionResult(channel => Created($"/channels/{channel.ChannelId}", channel));

    [HttpGet("{channelId:guid}")]
    [ProducesResponseType<ChannelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid channelId, CancellationToken ct) =>
        (await channelService.GetAsync(channelId, ct)).ToActionResult();

    /// <summary>
    /// Resolves a public <c>@handle</c> to a channel — the endpoint that exists
    /// so nothing else has to. Handles are mutable, so services reference
    /// channels by id only and a handle is translated exactly once, here.
    /// </summary>
    [HttpGet("by-handle/{handle}")]
    [ProducesResponseType<ChannelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByHandle(string handle, CancellationToken ct) =>
        (await channelService.GetByHandleAsync(handle, ct)).ToActionResult();

    /// <summary>Fifty videos, fifty channel ids, one call. See <see cref="UsersController"/>.</summary>
    [HttpPost("batch")]
    [ProducesResponseType<IReadOnlyList<ChannelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBatch(BatchLookupRequest request, CancellationToken ct) =>
        (await channelService.GetBatchAsync(request.Ids, ct)).ToActionResult();
}
