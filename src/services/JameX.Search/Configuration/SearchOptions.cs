namespace JameX.Search.Configuration;

/// <summary>Everything tunable about the inverted index, owned by this service alone.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    public string IndexTableName { get; set; } = "jamex-search-index";

    /// <summary>A ceiling on results returned, for the same reason Catalog caps a feed page.</summary>
    public int MaxResults { get; set; } = 20;
}
