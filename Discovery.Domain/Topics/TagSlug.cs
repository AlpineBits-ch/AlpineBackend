using System.Globalization;
using System.Text;

namespace Discovery.Domain.Topics;

/// <summary>Folds free text into a tag slug.</summary>
public static class TagSlug
{
    public const int MaxLength = 48;

    /// <summary>The slug, or null when nothing survives normalization.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var builder = new StringBuilder(raw.Length);
        var pendingSeparator = false;

        foreach (var rune in raw.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;

            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                pendingSeparator = false;
                builder.Append(Rune.ToLowerInvariant(rune));
                continue;
            }

            // Only whitespace and hyphens separate. Everything else drops silently, so "D&D" is
            // one word while "sci-fi" stays two.
            if (Rune.IsWhiteSpace(rune) || rune.Value == '-') pendingSeparator = true;
        }

        var slug = builder.ToString();
        if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('-');
        return slug.Length == 0 ? null : slug;
    }
}
