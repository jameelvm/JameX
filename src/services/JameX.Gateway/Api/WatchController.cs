using JameX.Contracts.Dtos;
using JameX.Gateway.Services;
using JameX.ServiceDefaults.Application;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Gateway.Api;

/// <summary>
/// The one native endpoint the Gateway serves itself rather than proxying —
/// everything else under <c>/api</c> is YARP forwarding to an owning service.
/// </summary>
[ApiController]
[Route("api/watch")]
[Produces("application/json")]
public sealed class WatchController(IWatchAggregationService watch) : ControllerBase
{
    [HttpGet("{videoId:guid}")]
    [ProducesResponseType<VideoDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid videoId, CancellationToken ct) =>
        (await watch.GetWatchPageAsync(videoId, ct)).ToActionResult();
}
