using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using JameX.Search.Configuration;
using JameX.Search.Domain;
using Microsoft.Extensions.Options;

namespace JameX.Search.Repositories;

/// <summary>One hit: which video, and how strongly it matched.</summary>
public sealed record SearchMatch(Guid VideoId, double Score);

/// <summary>
/// Every way this service reaches <c>jamex-search-index</c> — the doc's
/// inverted index, exactly as specified: <c>term → videoId → frequency, field</c>.
/// <para>
/// A single term is one partition read. A multi-term query is chapter 3's
/// stated cost of this design made concrete: there is no single query that
/// answers "which videos match all of these terms" — each term is queried
/// independently and the results are intersected in application memory.
/// </para>
/// </summary>
public interface ISearchIndexRepository
{
    /// <summary>
    /// Indexes (or re-indexes) one video. A term that appears in more than one
    /// field is recorded once, under whichever field ranks highest — see
    /// remarks in the implementation — with frequency summed across every
    /// field it occurred in.
    /// </summary>
    Task IndexAsync(
        Guid videoId, string title, string? description, string[] tags,
        DateTimeOffset indexedAt, CancellationToken ct);

    /// <summary>
    /// AND semantics: a video must match every term in the query to be
    /// returned, ranked by summed frequency across the matched terms — the
    /// only relevance signal this index has.
    /// </summary>
    Task<IReadOnlyList<SearchMatch>> SearchAsync(string query, int limit, CancellationToken ct);

    /// <summary>Removes every posting for a video — the <c>VideoDeleted</c> teardown.</summary>
    Task DeleteAllForVideoAsync(Guid videoId, CancellationToken ct);
}

internal sealed class SearchIndexRepository(
    IAmazonDynamoDB dynamo, IOptions<SearchOptions> options) : ISearchIndexRepository
{
    private const string TitleField = "title";
    private const string DescriptionField = "description";
    private const string TagsField = "tags";

    // Priority decides which field "wins" when a term appears in more than
    // one — a title hit is a stronger signal than a tag hit, which is a
    // stronger signal than a description hit.
    private static readonly Dictionary<string, int> FieldPriority = new()
    {
        [TitleField] = 3,
        [TagsField] = 2,
        [DescriptionField] = 1
    };

    private readonly string _table = options.Value.IndexTableName;

    public async Task IndexAsync(
        Guid videoId, string title, string? description, string[] tags,
        DateTimeOffset indexedAt, CancellationToken ct)
    {
        var postings = new Dictionary<string, (int Frequency, string Field)>(StringComparer.Ordinal);

        Accumulate(postings, TitleField, title);
        Accumulate(postings, DescriptionField, description);
        Accumulate(postings, TagsField, string.Join(' ', tags));

        if (postings.Count == 0) return;

        var writes = postings.Select(p => new WriteRequest
        {
            PutRequest = new PutRequest
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["term"] = new(p.Key),
                    ["videoId"] = new(videoId.ToString()),
                    ["frequency"] = new() { N = p.Value.Frequency.ToString() },
                    ["field"] = new(p.Value.Field),
                    ["indexedAt"] = new(indexedAt.ToString("O"))
                }
            }
        }).ToList();

        // BatchWriteItem caps at 25 requests per call.
        foreach (var chunk in writes.Chunk(25))
        {
            await dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { [_table] = chunk.ToList() }
            }, ct);
        }
    }

    private static void Accumulate(
        Dictionary<string, (int Frequency, string Field)> postings, string field, string? text)
    {
        foreach (var (term, count) in Tokenizer.CountTerms(text))
        {
            if (postings.TryGetValue(term, out var existing))
            {
                var winningField = FieldPriority[field] > FieldPriority[existing.Field] ? field : existing.Field;
                postings[term] = (existing.Frequency + count, winningField);
            }
            else
            {
                postings[term] = (count, field);
            }
        }
    }

    public async Task<IReadOnlyList<SearchMatch>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var terms = Tokenizer.ExtractTerms(query).ToArray();
        if (terms.Length == 0) return [];

        // The fan-out: one Query per term, run concurrently. Each is a single
        // partition read — the whole reason this index scales for reads.
        var perTerm = await Task.WhenAll(terms.Select(t => QueryTermAsync(t, ct)));

        // The intersection: a video only counts if it turned up under every
        // term queried, not just one of them.
        var scoreByVideo = new Dictionary<Guid, double>();
        var matchCountByVideo = new Dictionary<Guid, int>();

        foreach (var postings in perTerm)
        {
            foreach (var (videoId, frequency) in postings)
            {
                scoreByVideo[videoId] = scoreByVideo.GetValueOrDefault(videoId) + frequency;
                matchCountByVideo[videoId] = matchCountByVideo.GetValueOrDefault(videoId) + 1;
            }
        }

        return matchCountByVideo
            .Where(kv => kv.Value == terms.Length)
            .Select(kv => new SearchMatch(kv.Key, scoreByVideo[kv.Key]))
            .OrderByDescending(m => m.Score)
            .Take(limit)
            .ToArray();
    }

    private async Task<IReadOnlyList<(Guid VideoId, int Frequency)>> QueryTermAsync(string term, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = _table,
            KeyConditionExpression = "term = :term",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":term"] = new(term) }
        }, ct);

        return response.Items
            .Select(item => (Guid.Parse(item["videoId"].S), int.Parse(item["frequency"].N)))
            .ToArray();
    }

    public async Task DeleteAllForVideoAsync(Guid videoId, CancellationToken ct)
    {
        // The base table is keyed by term first, so finding every posting for
        // one video needs the by-video GSI — the same reason
        // UserReactionRepository in Engagement has one.
        var response = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = _table,
            IndexName = "by-video",
            KeyConditionExpression = "videoId = :videoId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":videoId"] = new(videoId.ToString())
            },
            ProjectionExpression = "term, videoId"
        }, ct);

        if (response.Items.Count == 0) return;

        foreach (var chunk in response.Items.Chunk(25))
        {
            await dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    // Deletes go through the base table's key (term + videoId),
                    // never the GSI — a GSI has no delete API of its own.
                    [_table] = chunk.Select(item => new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest { Key = item }
                    }).ToList()
                }
            }, ct);
        }
    }
}
