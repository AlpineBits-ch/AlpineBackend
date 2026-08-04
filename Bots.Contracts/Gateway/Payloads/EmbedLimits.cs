using System.Globalization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Discord's documented embed size limits, and the clamping that enforces them.</summary>
public static class EmbedLimits
{
    public const int Title = 256;
    public const int Description = 4096;
    public const int FieldName = 256;
    public const int FieldValue = 1024;
    public const int FieldCount = 25;
    public const int FooterText = 2048;
    public const int AuthorName = 256;
    public const int ProviderName = 256;

    /// <summary>Sum of title, description, field names and values, footer text and author name
    /// across <b>every</b> embed on one message.</summary>
    public const int TotalPerMessage = 6000;

    /// <summary>Discord unfurls at most this many links per message.</summary>
    public const int MaxUnfurledPerMessage = 5;

    /// <summary>Clamps one embed in place to the per-field limits.</summary>
    public static EmbedPayload Clamp(EmbedPayload embed)
    {
        embed.Title = Truncate(embed.Title, Title);
        embed.Description = Truncate(embed.Description, Description);

        if (embed.Author is not null)
            embed.Author.Name = Truncate(embed.Author.Name, AuthorName) ?? "";

        if (embed.Provider is not null)
            embed.Provider.Name = Truncate(embed.Provider.Name, ProviderName) ?? "";

        if (embed.Footer is not null)
            embed.Footer.Text = Truncate(embed.Footer.Text, FooterText) ?? "";

        if (embed.Fields.Count > FieldCount)
            embed.Fields = embed.Fields.Take(FieldCount).ToList();

        foreach (var field in embed.Fields)
        {
            field.Name = Truncate(field.Name, FieldName) ?? "";
            field.Value = Truncate(field.Value, FieldValue) ?? "";
        }

        return embed;
    }

    /// <summary>The characters that count toward <see cref="TotalPerMessage"/> for one embed.</summary>
    public static int CountedLength(EmbedPayload embed) =>
        (embed.Title?.Length ?? 0)
        + (embed.Description?.Length ?? 0)
        + (embed.Author?.Name.Length ?? 0)
        + (embed.Footer?.Text.Length ?? 0)
        + embed.Fields.Sum(f => f.Name.Length + f.Value.Length);

    /// <summary>
    /// Takes embeds in order until the combined budget is spent, dropping the rest whole.
    /// </summary>
    public static List<EmbedPayload> ClampTotal(IEnumerable<EmbedPayload> embeds)
    {
        var kept = new List<EmbedPayload>();
        var budget = TotalPerMessage;

        foreach (var embed in embeds)
        {
            var cost = CountedLength(Clamp(embed));
            if (cost > budget) break;
            budget -= cost;
            kept.Add(embed);
        }

        return kept;
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null || value.Length <= max) return value;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var lastSafeEnd = 0;

        while (enumerator.MoveNext())
        {
            var end = enumerator.ElementIndex + enumerator.GetTextElement().Length;
            if (end > max) break;
            lastSafeEnd = end;
        }

        return value[..lastSafeEnd];
    }
}

/// <summary>
/// Converts between the <c>#rrggbb</c> strings bots already send in <see cref="EmbedPayload.Color"/>
/// and the integer Discord's own wire format uses.
/// </summary>
public static class EmbedColor
{
    public static string FromRgb(int r, int g, int b) =>
        $"#{Math.Clamp(r, 0, 255):x2}{Math.Clamp(g, 0, 255):x2}{Math.Clamp(b, 0, 255):x2}";

    /// <summary>Parses <c>#rrggbb</c>, <c>rrggbb</c> or a decimal integer.</summary>
    public static int? ToInt(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;

        var text = color.Trim().TrimStart('#');

        if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec is >= 0 and <= 0xFFFFFF)
            return dec;

        return null;
    }
}
