using System.Net;
using System.Net.Http.Json;
using JameX.Contracts;
using JameX.Contracts.Dtos;
using JameX.ServiceDefaults.Hosting;

namespace JameX.Gateway.Clients;

/// <summary>
/// The Gateway's read of Engagement — counts and the caller's own reaction.
/// Both a fault and Engagement simply being unreachable degrade the watch
/// page rather than break it: a video is still watchable with counts and a
/// reaction the page doesn't know about yet.
/// </summary>
public interface IEngagementReadClient
{
    Task<EngagementCounts?> GetCountsAsync(Guid videoId, CancellationToken ct);

    /// <summary>
    /// Null for an anonymous caller (nobody to have a reaction) or one who
    /// simply hasn't reacted — Engagement's own endpoint does not distinguish
    /// the two, and neither does the watch page.
    /// </summary>
    Task<ReactionKind?> GetMyReactionAsync(Guid videoId, Guid? callerId, CancellationToken ct);
}

internal sealed class EngagementReadClient(HttpClient http, ILogger<EngagementReadClient> logger)
    : IEngagementReadClient
{
    public async Task<EngagementCounts?> GetCountsAsync(Guid videoId, CancellationToken ct)
    {
        try
        {
            return await http.GetFromJsonAsync<EngagementCounts>($"videos/{videoId}/counts", ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach Engagement for counts on video {VideoId}", videoId);
            return null;
        }
    }

    public async Task<ReactionKind?> GetMyReactionAsync(Guid videoId, Guid? callerId, CancellationToken ct)
    {
        if (callerId is null) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"videos/{videoId}/reactions/me");
            request.Headers.Add(HeaderCurrentUser.HeaderName, callerId.Value.ToString());

            var response = await http.SendAsync(request, ct);

            // A viewer who hasn't reacted gets 204 from Engagement's own
            // null-output formatter — not an error, just "nothing to report".
            if (response.StatusCode == HttpStatusCode.NoContent) return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ReactionKind>(ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach Engagement for viewer reaction on video {VideoId}", videoId);
            return null;
        }
    }
}
