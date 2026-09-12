using JameX.Contracts.Dtos;
using JameX.Search.Clients;
using JameX.Search.Configuration;
using JameX.Search.Domain;
using JameX.Search.Repositories;
using JameX.ServiceDefaults.Application;
using Microsoft.Extensions.Options;

namespace JameX.Search.Services;

public interface ISearchQueryService
{
    Task<OperationResult<IReadOnlyList<SearchHit>>> SearchAsync(string query, int limit, CancellationToken ct);
}

internal sealed class SearchQueryService(
    ISearchIndexRepository index,
    ICatalogClient catalog,
    IOptions<SearchOptions> options) : ISearchQueryService
{
    public async Task<OperationResult<IReadOnlyList<SearchHit>>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return OperationResult<IReadOnlyList<SearchHit>>.Invalid("q", "A search query is required.");

        limit = limit is < 1 or > 50 ? options.Value.MaxResults : limit;

        var matches = await index.SearchAsync(query, limit, ct);
        if (matches.Count == 0) return OperationResult<IReadOnlyList<SearchHit>>.Success([]);

        // The terms behind MatchedOn — same extraction the index itself used,
        // so what is reported is exactly what was matched, not a re-guess.
        var matchedOn = string.Join(", ", Tokenizer.ExtractTerms(query));

        var videos = await catalog.GetManyAsync(matches.Select(m => m.VideoId).ToArray(), ct);
        var videosById = videos.ToDictionary(v => v.VideoId);

        // Ordering comes from the index (highest score first); a match whose
        // video Catalog no longer has is dropped rather than shown with holes
        // — see ICatalogClient's remarks on this gap.
        var hits = matches
            .Where(m => videosById.ContainsKey(m.VideoId))
            .Select(m =>
            {
                var video = videosById[m.VideoId];
                return new SearchHit(
                    video.VideoId, video.Title, video.ThumbnailUrl, video.DurationSeconds,
                    video.PublishedAt, m.Score, matchedOn);
            })
            .ToArray();

        return OperationResult<IReadOnlyList<SearchHit>>.Success(hits);
    }
}
