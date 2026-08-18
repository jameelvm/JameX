namespace JameX.Catalog.Validation;

/// <summary>
/// Limits the read API enforces, in one place so every endpoint agrees.
/// </summary>
public static class CatalogRules
{
    public const int DefaultPageSize = 20;

    /// <summary>
    /// A hard ceiling, not a suggestion. Without it a caller asks for
    /// <c>pageSize=1000000</c> and one request materialises the whole
    /// catalogue into memory — the cheapest denial of service there is.
    /// </summary>
    public const int MaxPageSize = 50;

    /// <summary>Matches Identity's batch cap, for the same reason.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>
    /// How long a cached watch page survives without being touched.
    /// <para>
    /// Short enough that a missed invalidation self-heals in minutes rather
    /// than persisting until someone notices; long enough that a popular video
    /// is served from memory rather than Postgres on every view.
    /// </para>
    /// </summary>
    public static readonly TimeSpan VideoCacheTtl = TimeSpan.FromMinutes(5);

    public static int NormalisePage(int page) => page < 1 ? 1 : page;

    public static int NormalisePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}
