namespace JameX.Engagement.Validation;

/// <summary>
/// Limits the comments API enforces, mirroring Catalog's <c>CatalogRules</c>.
/// </summary>
public static class EngagementRules
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>
    /// Matches <c>Comment.Text</c>'s column length. Enforced here too so an
    /// over-long comment gets a 400 instead of a database error.
    /// </summary>
    public const int MaxCommentLength = 10_000;

    public static int NormalisePage(int page) => page < 1 ? 1 : page;

    public static int NormalisePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}
