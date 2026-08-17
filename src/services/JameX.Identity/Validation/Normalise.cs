using System.Text.RegularExpressions;

namespace JameX.Identity.Validation;

/// <summary>
/// Input normalisation and validation, in one place so every write path agrees
/// on what a valid value is — and, critically, so the read path normalises
/// identically. A handle looked up without being lower-cased would miss a row
/// that is plainly there.
/// </summary>
public static partial class Normalise
{
    /// <summary>
    /// One batch may not exceed this. An unbounded <c>WHERE id = ANY(...)</c>
    /// is a denial-of-service vector and blows past what a single index scan
    /// should be doing per request.
    /// </summary>
    public const int MaxBatchSize = 100;

    /// <summary>
    /// Lower-cased and trimmed, so the unique index enforces case-insensitive
    /// uniqueness — see <see cref="Domain.User.Email"/>.
    /// </summary>
    public static string Email(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Strips a leading <c>@</c> and lower-cases. The <c>@</c> is display
    /// syntax, not part of the value; storing it would mean removing it on
    /// every lookup.
    /// </summary>
    public static string Handle(string value) => value.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Deliberately permissive — a full RFC 5322 check rejects valid addresses.</summary>
    public static bool IsPlausibleEmail(string value) =>
        value.Length is >= 3 and <= 320
        && value.IndexOf('@') > 0
        && value.IndexOf('@') < value.Length - 1
        && !value.Contains(' ');

    /// <summary>Handles appear in URLs, so the character set is restrictive.</summary>
    public static bool IsValidHandle(string value) => HandlePattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,29}$")]
    private static partial Regex HandlePattern();
}
