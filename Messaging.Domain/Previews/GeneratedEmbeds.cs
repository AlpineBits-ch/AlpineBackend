using System.Text.Json;
using Bots.Contracts.Gateway.Payloads;

namespace Messaging.Domain.Previews;

/// <summary>
/// Splits a message's stored embed array into the part the author wrote and the part the unfurler
/// produced.
///
/// <para>The distinction is what stops a re-unfurl from destroying a bot's hand-written card, and
/// what lets suppression drop generated previews while leaving author content alone. It is carried
/// by <see cref="EmbedFlags.ServerGenerated"/> on each embed rather than by position, because the
/// order of the array is a rendering concern and gets rewritten.</para>
/// </summary>
public static class GeneratedEmbeds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses stored embed JSON. Returns an empty list for null, whitespace, or anything that does
    /// not parse - a message whose embeds column is corrupt should still be readable and still be
    /// unfurlable, not throw on every read.
    /// </summary>
    public static List<EmbedPayload> Parse(string? embedsJson)
    {
        if (string.IsNullOrWhiteSpace(embedsJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<EmbedPayload>>(embedsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<EmbedPayload> embeds) =>
        JsonSerializer.Serialize(embeds, Json);

    /// <summary>Whether the author put any embeds on this message themselves. When true the
    /// unfurler stands down entirely - Discord does not add link previews to a bot message that
    /// already carries its own cards, and merging the two has no sensible ordering.</summary>
    public static bool HasAuthorEmbeds(string? embedsJson) =>
        Parse(embedsJson).Any(e => !e.IsGenerated);

    /// <summary>
    /// The stored array with every generated embed removed, serialized.
    ///
    /// <para>Returns <c>"[]"</c> rather than null when nothing is left: null is the patch
    /// convention for "leave the stored embeds alone", which is the opposite of what a caller
    /// clearing previews means.</para>
    /// </summary>
    public static string RemoveGenerated(string? embedsJson) =>
        Serialize(Parse(embedsJson).Where(e => !e.IsGenerated));

    /// <summary>
    /// Author embeds preserved in order, followed by the freshly generated ones.
    ///
    /// <para>Generated embeds go last so that a preview appearing a second after send never
    /// reorders content the author already saw - the card materializes below their message rather
    /// than shoving it down the screen.</para>
    /// </summary>
    public static string Merge(string? existingJson, IEnumerable<EmbedPayload> generated)
    {
        var authored = Parse(existingJson).Where(e => !e.IsGenerated);

        var stamped = generated.Select(embed =>
        {
            embed.Flags |= EmbedFlags.ServerGenerated;
            return embed;
        });

        return Serialize(EmbedLimits.ClampTotal(authored.Concat(stamped)));
    }
}
