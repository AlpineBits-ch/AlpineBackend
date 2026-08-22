namespace Discovery.Domain.Topics;

/// <summary>A parsed topic plus the text the user actually typed, which the slug discards.</summary>
public readonly record struct TopicInput(TopicRef Topic, string? RawText)
{
    /// <summary>
    /// Parses a batch of wire topic strings ("tag:..." or "game:..."), pairing each with the raw
    /// text after its colon so a minted tag gets a readable display name. Stops at the first string
    /// that does not parse and reports it via <paramref name="badRef"/> rather than throwing, so a
    /// caller can refuse the whole request before writing anything.
    /// </summary>
    public static bool TryParseAll(IEnumerable<string> raw, out IReadOnlyList<TopicInput> topics, out string? badRef)
    {
        var results = new List<TopicInput>();
        foreach (var value in raw)
        {
            if (!TopicRef.TryParse(value, out var topic))
            {
                topics = [];
                badRef = value;
                return false;
            }

            var separator = value.IndexOf(':');
            results.Add(new TopicInput(topic, separator >= 0 ? value[(separator + 1)..] : value));
        }

        topics = results;
        badRef = null;
        return true;
    }
}
