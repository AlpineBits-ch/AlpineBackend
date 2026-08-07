using System.Text;

namespace Guild.Domain.Services;

/// <summary>
/// Turns an ingredient line into the noun it is about, and decides whether two such nouns are the
/// same thing.
/// </summary>
public static class IngredientMatch
{
    /// <summary>Measure words consumed after a leading quantity.</summary>
    private static readonly HashSet<string> Units = new(StringComparer.Ordinal)
    {
        "g", "gram", "grams", "kg", "mg", "oz", "lb", "lbs",
        "ml", "l", "cl", "dl", "litre", "litres", "liter", "liters",
        "tsp", "tsps", "teaspoon", "teaspoons", "tbsp", "tbsps", "tablespoon", "tablespoons",
        "cup", "cups", "pack", "packs", "packet", "packets", "punnet", "punnets",
        "tin", "tins", "can", "cans", "jar", "jars", "bottle", "bottles", "box", "boxes",
        "bag", "bags", "bunch", "bunches", "clove", "cloves", "slice", "slices",
        "pinch", "pinches", "dash", "dashes", "splash", "splashes", "drizzle",
        "handful", "handfuls", "piece", "pieces", "sprig", "sprigs", "stick", "sticks",
        "head", "heads", "knob", "knobs", "sheet", "sheets",
    };

    /// <summary>Words that stand in for a number.</summary>
    private static readonly HashSet<string> QuantityWords = new(StringComparer.Ordinal)
    {
        "a", "an", "some", "few", "couple", "half",
    };

    /// <summary>
    /// Reduces an ingredient line to the noun it is about: lowercased, stripped of a leading
    /// quantity and measure word, stripped of punctuation, and singularized on a trailing "s" only.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var tokens = Tokenize(text);
        if (tokens.Count == 0) return string.Empty;

        var index = 0;

        // Order matters and is fixed: quantity, then at most one unit, then at most one "of".
        while (index < tokens.Count && QuantityWords.Contains(tokens[index])) index++;
        if (index < tokens.Count && Units.Contains(tokens[index])) index++;
        if (index < tokens.Count && tokens[index] == "of") index++;

        // Everything consumed means the line was only ever a quantity.
        if (index >= tokens.Count) index = 0;

        var kept = tokens.GetRange(index, tokens.Count - index);
        kept[^1] = Singularize(kept[^1]);

        return string.Join(' ', kept);
    }

    /// <summary>
    /// Whether two names refer to the same thing: equal after normalization, or one occurring
    /// inside the other as a whole run of words ("onion" inside "spring onion").
    /// </summary>
    public static bool Matches(string? ingredientMatchName, string? candidateName)
    {
        var left = Normalize(ingredientMatchName);
        var right = Normalize(candidateName);

        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;

        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return ContainsRun(leftWords, rightWords) || ContainsRun(rightWords, leftWords);
    }

    /// <summary>Whether <paramref name="needle"/> appears in <paramref name="haystack"/> as a
    /// contiguous run of whole words.</summary>
    private static bool ContainsRun(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var all = true;
            for (var offset = 0; offset < needle.Length && all; offset++)
                all = haystack[start + offset] == needle[offset];

            if (all) return true;
        }

        return false;
    }

    /// <summary>Lowercases, drops punctuation, and splits a digit-led token like "500g" so the unit
    /// can be recognised on its own.</summary>
    private static List<string> Tokenize(string text)
    {
        var cleaned = new StringBuilder(text.Length);

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) cleaned.Append(ch);
            else cleaned.Append(' ');
        }

        var tokens = new List<string>();

        foreach (var raw in cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!char.IsDigit(raw[0]))
            {
                tokens.Add(raw);
                continue;
            }

            // "500g" is one token to the splitter and two to a reader.
            var cut = 0;
            while (cut < raw.Length && char.IsDigit(raw[cut])) cut++;

            if (cut < raw.Length) tokens.Add(raw[cut..]);
        }

        return tokens;
    }

    /// <summary>Drops a trailing "s", and only a trailing "s".</summary>
    private static string Singularize(string word) =>
        word.Length > 3
        && word[^1] == 's'
        && !word.EndsWith("ss", StringComparison.Ordinal)
        && !word.EndsWith("us", StringComparison.Ordinal)
        && !word.EndsWith("is", StringComparison.Ordinal)
            ? word[..^1]
            : word;
}
