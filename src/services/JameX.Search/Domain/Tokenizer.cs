using System.Text.RegularExpressions;

namespace JameX.Search.Domain;

/// <summary>
/// Turns free text into search terms. Deliberately the simplest thing that
/// works: lowercase, split on runs of letters/digits, count occurrences.
/// <para>
/// No stemming ("running" and "run" are unrelated terms here), no stopword
/// removal (a query for "the guitar" fans out to two partitions, one of them
/// -- "the" -- pathologically hot), no synonym expansion. Real search engines
/// spend enormous effort on exactly these gaps; naming them instead of hiding
/// them is the honest half of the inverted-index-vs-FTS comparison this phase
/// makes — see the Postgres side in Catalog for what a mature text-search
/// engine gets for free that this does not.
/// </para>
/// </summary>
public static partial class Tokenizer
{
    /// <summary>A term shorter than this carries too little signal to index — "a", "i", "3".</summary>
    private const int MinTermLength = 2;

    /// <summary>Term → how many times it occurred in <paramref name="text"/>.</summary>
    public static IReadOnlyDictionary<string, int> CountTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, int>();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match match in TermPattern().Matches(text.ToLowerInvariant()))
        {
            if (match.Length < MinTermLength) continue;
            counts[match.Value] = counts.GetValueOrDefault(match.Value) + 1;
        }

        return counts;
    }

    /// <summary>The distinct terms in a search query — the same rules as indexing, so a query matches what was indexed.</summary>
    public static IReadOnlyCollection<string> ExtractTerms(string query) => CountTerms(query).Keys.ToArray();

    // Runs of Unicode letters or digits — matches "guitar" and "guitar101"
    // as single terms and splits on everything else (punctuation, whitespace).
    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex TermPattern();
}
