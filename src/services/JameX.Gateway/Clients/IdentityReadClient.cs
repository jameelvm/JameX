using System.Net;
using System.Net.Http.Json;
using JameX.Contracts.Dtos;

namespace JameX.Gateway.Clients;

/// <summary>The Gateway's read of Identity — just the channel name for the byline.</summary>
public interface IIdentityReadClient
{
    Task<ChannelDto?> GetChannelAsync(Guid channelId, CancellationToken ct);
}

internal sealed class IdentityReadClient(HttpClient http, ILogger<IdentityReadClient> logger)
    : IIdentityReadClient
{
    public async Task<ChannelDto?> GetChannelAsync(Guid channelId, CancellationToken ct)
    {
        try
        {
            var response = await http.GetAsync($"channels/{channelId}", ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<ChannelDto>(ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach Identity for channel {ChannelId}", channelId);
            return null;
        }
    }
}
