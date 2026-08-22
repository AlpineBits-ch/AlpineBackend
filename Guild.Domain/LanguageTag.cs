using System.Text;

namespace Guild.Domain;

/// <summary>
/// BCP-47 shape only, not validated against the subtag registry: a guild may declare any
/// well-formed tag, and the client offers a curated list (spec 5.1).
/// </summary>
public static class LanguageTag
{
    public const int MaxOtherLanguages = 4;

    private const int MaxSubtagLength = 8;

    public static bool IsWellFormed(string? tag) => Normalize(tag) is not null;

    /// <summary>Canonical case: language lowercase, script title, region upper. Null when malformed.</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var parts = tag.Trim().Split('-');
        if (parts.Length == 0) return null;

        var primary = parts[0];
        if (primary.Length is < 2 or > MaxSubtagLength || !primary.All(char.IsAsciiLetter)) return null;

        var builder = new StringBuilder(primary.ToLowerInvariant());

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > MaxSubtagLength || !part.All(char.IsAsciiLetterOrDigit)) return null;

            builder.Append('-').Append(CaseSubtag(part));
        }

        return builder.ToString();
    }

    private static string CaseSubtag(string part) => part.Length switch
    {
        4 when part.All(char.IsAsciiLetter) => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant(),
        2 when part.All(char.IsAsciiLetter) => part.ToUpperInvariant(),
        _ => part.ToLowerInvariant(),
    };

    /// <summary>
    /// Normalizes a whole declaration. The primary never appears in the others, and the others
    /// carry no duplicates, so the match set is just {primary} union others.
    /// </summary>
    public static bool TryNormalizeSet(
        string? primary,
        IEnumerable<string>? others,
        out string normalizedPrimary,
        out List<string> normalizedOthers,
        out string? problem)
    {
        normalizedPrimary = string.Empty;
        normalizedOthers = [];

        var canonicalPrimary = Normalize(primary);
        if (canonicalPrimary is null)
        {
            problem = $"'{primary}' is not a well-formed language tag.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { canonicalPrimary };
        var collected = new List<string>();

        foreach (var candidate in others ?? [])
        {
            var canonical = Normalize(candidate);
            if (canonical is null)
            {
                problem = $"'{candidate}' is not a well-formed language tag.";
                return false;
            }

            if (seen.Add(canonical)) collected.Add(canonical);
        }

        if (collected.Count > MaxOtherLanguages)
        {
            problem = $"A guild may list at most {MaxOtherLanguages} other languages.";
            return false;
        }

        normalizedPrimary = canonicalPrimary;
        normalizedOthers = collected;
        problem = null;
        return true;
    }
}
