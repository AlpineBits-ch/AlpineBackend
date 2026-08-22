namespace Discovery.Domain.Topics;

public enum TopicKind
{
    Game,
    Tag,
}

/// <summary>A topic on a listing or a profile. Games resolve against the mirrored catalog.</summary>
public readonly record struct TopicRef(TopicKind Kind, string Id)
{
    public static TopicRef Parse(string raw) =>
        TryParse(raw, out var topic) ? topic : throw new FormatException($"Not a topic reference: {raw}");

    public static bool TryParse(string? raw, out TopicRef topic)
    {
        topic = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var separator = raw.IndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1) return false;

        var kind = raw[..separator];
        var id = raw[(separator + 1)..];

        if (kind.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            topic = new TopicRef(TopicKind.Game, id);
            return true;
        }

        if (!kind.Equals("tag", StringComparison.OrdinalIgnoreCase)) return false;

        var slug = TagSlug.Normalize(id);
        if (slug is null) return false;

        topic = new TopicRef(TopicKind.Tag, slug);
        return true;
    }

    public override string ToString() => $"{(Kind == TopicKind.Game ? "game" : "tag")}:{Id}";
}
