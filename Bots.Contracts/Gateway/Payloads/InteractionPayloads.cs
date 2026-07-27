using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Dispatched to the bot as INTERACTION_CREATE when a slash command is invoked.</summary>
public class InteractionPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("application_id")]
    public string ApplicationId { get; set; } = "";

    /// <summary>2 = APPLICATION_COMMAND - the only interaction type this compat layer produces
    /// (no components/autocomplete, per the "slash commands only" v1 scope decision).</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = 2;

    [JsonPropertyName("data")]
    public InteractionDataPayload Data { get; set; } = null!;

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("member")]
    public GatewayMemberPayload? Member { get; set; }

    /// <summary>Secret used to authenticate the callback/followup calls - there's no bot-token
    /// auth on those endpoints, same as real Discord.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
}

public class InteractionDataPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>1 = CHAT_INPUT (slash command) - the only command type supported.</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = 1;

    [JsonPropertyName("options")]
    public List<InteractionOptionPayload> Options { get; set; } = new();
}

public class InteractionOptionPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

/// <summary>Inbound body for POST .../interactions/{id}/{token}/callback - the bot's response.</summary>
public class InteractionCallbackPayload
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("data")]
    public InteractionResponseDataPayload? Data { get; set; }
}

/// <summary>Shared shape for both the callback's `data` field and a followup message body -
/// Discord's real webhook-execute endpoint takes the same {content, flags} shape directly.</summary>
public class InteractionResponseDataPayload
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>64 = EPHEMERAL. Accepted but not enforced in v1 - posts as a normal channel
    /// message, since there's no "only visible to the invoker" concept in venta's channel model
    /// today. A known, deliberate limitation, not an oversight.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}
