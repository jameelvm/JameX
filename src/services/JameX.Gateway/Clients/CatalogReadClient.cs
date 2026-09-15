using System.Net;
using System.Net.Http.Json;
using JameX.Contracts.Dtos;

namespace JameX.Gateway.Clients;

/// <summary>
/// The Gateway's read of Catalog — the primary source for the watch page.
/// Unlike <see cref="IEngagementReadClient"/> and <see cref="IIdentityReadClient"/>,
/// a failure here is not something the page can degrade gracefully around:
/// with no video there is nothing to aggregate onto, so this one propagates
/// rather than swallowing the exception.
/// </summary>
public interface ICatalogReadClient
{
    /// <summary>Null means the video does not exist — a real 404, not a fault.</summary>
    Task<VideoDetail?> GetVideoAsync(Guid videoId, CancellationToken ct);
}

internal sealed class CatalogReadClient(HttpClient http) : ICatalogReadClient
{
    public async Task<VideoDetail?> GetVideoAsync(Guid videoId, CancellationToken ct)
    {
        var response = await http.GetAsync($"videos/{videoId}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<VideoDetail>(ct);
    }
}
