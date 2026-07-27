using System.Text;
using Bots.Contracts.Gateway.Payloads;

namespace Bots.Application.Endpoints.Discord;

/// <summary>
/// Embeds are now stored structurally on the message (Message.EmbedsJson) for clients that render
/// rich cards. This flattener produces a plain-text fallback for Content when a bot's response has
/// embeds but no `content` at all (common for status/info commands) - so unmodified/legacy clients
/// still show something readable instead of a blank message.
/// </summary>
public static class EmbedFlattener
{
    public static string Flatten(string? content, IReadOnlyList<EmbedPayload>? embeds)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(content)) builder.AppendLine(content);

        if (embeds is null) return builder.ToString().TrimEnd();

        foreach (var embed in embeds)
        {
            if (builder.Length > 0) builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(embed.Author?.Name)) builder.AppendLine(embed.Author!.Name);
            if (!string.IsNullOrWhiteSpace(embed.Title)) builder.AppendLine($"**{embed.Title}**");
            if (!string.IsNullOrWhiteSpace(embed.Url)) builder.AppendLine(embed.Url);
            if (!string.IsNullOrWhiteSpace(embed.Description)) builder.AppendLine(embed.Description);

            foreach (var field in embed.Fields)
            {
                builder.AppendLine($"{field.Name}: {field.Value}");
            }

            if (!string.IsNullOrWhiteSpace(embed.Footer?.Text)) builder.AppendLine(embed.Footer!.Text);
        }

        return builder.ToString().TrimEnd();
    }
}
