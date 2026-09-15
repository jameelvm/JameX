namespace JameX.Gateway.Configuration;

/// <summary>
/// Where the BFF aggregation reaches each owning service. Grouped in one
/// class rather than three near-identical ones — every entry exists for the
/// same reason (the watch page's server-side fan-out) and is read the same
/// way, so three separate options classes would be ceremony, not clarity.
/// </summary>
public sealed class BffClientOptions
{
    public const string SectionName = "BffClients";

    public string CatalogBaseUrl { get; set; } = "http://localhost:8082";
    public string EngagementBaseUrl { get; set; } = "http://localhost:8084";
    public string IdentityBaseUrl { get; set; } = "http://localhost:8081";
}
