namespace JameX.Identity.Contracts;

/// <summary>
/// Request bodies stay here rather than in <c>JameX.Contracts</c>.
/// <para>
/// Contracts holds what crosses a service boundary — events, and the response
/// DTOs the Gateway aggregates. A request body is consumed by exactly one
/// service and read by a browser that does not share our types, so publishing
/// it would only invite another service to construct one and call in.
/// </para>
/// <para>
/// The service layer takes these types directly rather than defining a parallel
/// set of command objects. That is a deliberate stop: with a two-field payload
/// the extra indirection buys nothing. It would start to pay if a service method
/// ever needed inputs the HTTP body does not carry.
/// </para>
/// </summary>
public sealed record CreateUserRequest(string Email, string DisplayName);

public sealed record CreateChannelRequest(string Name, string Handle, string? AvatarUrl);

/// <summary>
/// Body of the batch lookup endpoints. Ids are sent in a POST body rather than
/// a query string because a watch page can ask for a hundred at once and URLs
/// have length limits — the request is still a read, despite the verb.
/// </summary>
public sealed record BatchLookupRequest(IReadOnlyList<Guid> Ids);
