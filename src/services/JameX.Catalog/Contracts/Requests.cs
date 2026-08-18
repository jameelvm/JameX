namespace JameX.Catalog.Contracts;

/// <summary>
/// Body of the batch lookup endpoint. Ids go in a POST body rather than a query
/// string because a feed can ask for a hundred at once — the request is still a
/// read, despite the verb.
/// </summary>
public sealed record BatchLookupRequest(IReadOnlyList<Guid> Ids);
