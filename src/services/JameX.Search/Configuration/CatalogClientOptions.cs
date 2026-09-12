namespace JameX.Search.Configuration;

/// <summary>
/// Where to reach Catalog's read API. The one place in this codebase a
/// backend service calls another backend service directly rather than
/// through the Gateway or an event — see <c>CatalogClient</c>'s remarks for
/// why that is fine here specifically.
/// </summary>
public sealed class CatalogClientOptions
{
    public const string SectionName = "CatalogClient";

    public string BaseUrl { get; set; } = "http://localhost:8082";
}
