using System.Text.Json;
using Bots.Contracts.Gateway.Payloads;

namespace Messaging.Domain.Previews;

/// <summary>
/// Splits a message's stored embed array into the part the author wrote and the part the unfurler
/// produced.
/// </summary>
public static class GeneratedEmbeds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses stored embed JSON.</summary>
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

    /// <summary>Whether the author put any embeds on this message themselves.</summary>
    public static bool HasAuthorEmbeds(string? embedsJson) =>
        Parse(embedsJson).Any(e => !e.IsGenerated);

    /// <summary>The stored array with every generated embed removed, serialized.</summary>
    public static string RemoveGenerated(string? embedsJson) =>
        Serialize(Parse(embedsJson).Where(e => !e.IsGenerated));

    /// <summary>Author embeds preserved in order, followed by the freshly generated ones.</summary>
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
