using JameX.Contracts.Dtos;
using JameX.Search.Services;
using JameX.ServiceDefaults.Application;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Search.Api;

/// <summary>Transport only — bind, call the service, translate the outcome.</summary>
[ApiController]
[Route("search")]
[Produces("application/json")]
public sealed class SearchController(ISearchQueryService search) : ControllerBase
{
    /// <summary>
    /// AND-semantics keyword search over the DynamoDB inverted index, ranked
    /// by summed term frequency — the honest limits of that model are
    /// documented on <c>Tokenizer</c> and <c>ISearchIndexRepository</c>. An
    /// out-of-range or omitted <paramref name="limit"/> falls back to
    /// <c>SearchOptions.MaxResults</c> — see the service.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SearchHit>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery] string q, [FromQuery] int limit, CancellationToken ct) =>
        (await search.SearchAsync(q, limit, ct)).ToActionResult();
}
