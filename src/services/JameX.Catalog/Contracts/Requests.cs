using JameX.Contracts;

namespace JameX.Catalog.Contracts;

/// <summary>
/// Body of the batch lookup endpoint. Ids go in a POST body rather than a query
/// string because a feed can ask for a hundred at once — the request is still a
/// read, despite the verb.
/// </summary>
public sealed record BatchLookupRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Partial update of a video's editable metadata.
/// <para>
/// Every property is nullable and null means "leave unchanged", which is what
/// makes this a PATCH rather than a PUT. A PUT would require the client to send
/// the whole resource back, and any field it forgot would be silently wiped.
/// </para>
/// </summary>
public sealed record UpdateVideoRequest(
    string? Title,
    string? Description,
    string? CategoryId,
    string[]? Tags,
    VideoPrivacy? Privacy);
