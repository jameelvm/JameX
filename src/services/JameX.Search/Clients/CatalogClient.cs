using System.Net.Http.Json;
using JameX.Contracts.Dtos;

namespace JameX.Search.Clients;

/// <summary>
/// Hydrates search results with the title/thumbnail/duration this service
/// does not own — Search stores only postings, never metadata.
/// <para>
/// This is a synchronous HTTP call from one backend service to another,
/// which the rest of the codebase deliberately avoids — see
/// <c>VideoEncoded</c>'s remarks for why the index-time path uses a
/// denormalised event instead. A search request is different: the caller is
/// already synchronously waiting on this HTTP response, so making it wait a
/// little longer for a second HTTP call costs nothing structurally, whereas
/// an async queue consumer blocking on Catalog's availability would turn one
/// service's outage into two.
/// </para>
/// </summary>
public interface ICatalogClient
{
    /// <summary>
    /// Resolves whichever of <paramref name="videoIds"/> Catalog still knows
    /// about. A missing id is silently absent — a video indexed but deleted
    /// before <c>VideoDeleted</c> was processed here is a real, if narrow,
    /// consistency gap, not a fault to raise.
    /// </summary>
    Task<IReadOnlyList<VideoSummary>> GetManyAsync(IReadOnlyCollection<Guid> videoIds, CancellationToken ct);
}

internal sealed record BatchLookupRequest(IReadOnlyList<Guid> Ids);

internal sealed class CatalogClient(HttpClient http, ILogger<CatalogClient> logger) : ICatalogClient
{
    public async Task<IReadOnlyList<VideoSummary>> GetManyAsync(
        IReadOnlyCollection<Guid> videoIds, CancellationToken ct)
    {
        if (videoIds.Count == 0) return [];

        try
        {
            var response = await http.PostAsJsonAsync(
                "videos/batch", new BatchLookupRequest(videoIds.ToArray()), ct);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<IReadOnlyList<VideoSummary>>(ct) ?? [];
        }
        catch (HttpRequestException ex)
        {
            // Catalog being unreachable degrades search to "no results" rather
            // than a 500 — the index itself is still fine, only the
            // hydration step failed.
            logger.LogWarning(ex, "Could not reach Catalog to hydrate {Count} search hits", videoIds.Count);
            return [];
        }
    }
}
