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

    /// <summary>discord.js reads data.channel?.id, NOT a flat channel_id field - a bare
    /// channel_id string (what real Discord used to send, now deprecated-but-still-present) is
    /// silently ignored by the client, leaving channelId/channel null. Keep channel_id too for
    /// any consumer that still reads the old flat field.</summary>
    [JsonPropertyName("channel")]
    public InteractionChannelPayload? Channel { get; set; }

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

    /// <summary>discord.js's BaseInteraction constructor calls `new PermissionsBitField(data.app_permissions)`
    /// unconditionally - there's no null-fallback, so this must always be a valid bitfield string.</summary>
    [JsonPropertyName("app_permissions")]
    public string AppPermissions { get; set; } = "0";

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "en-US";

    [JsonPropertyName("guild_locale")]
    public string? GuildLocale { get; set; }

    /// <summary>discord.js calls `data.entitlements.reduce(...)` unconditionally with no fallback
    /// - omitting this entirely crashes BaseInteraction's constructor (confirmed against the
    /// real discord.js source, packages/discord.js/src/structures/BaseInteraction.js). This
    /// project has no monetization/SKU concept, so it's always empty.</summary>
    [JsonPropertyName("entitlements")]
    public List<object> Entitlements { get; set; } = new();

    /// <summary>Passed straight into `new AuthorizingIntegrationOwners(client, data.authorizing_integration_owners)`,
    /// which does `this.data[key]` inside a loop - omitting this entirely (undefined) throws
    /// there too. An empty object is a valid, safe default.</summary>
    [JsonPropertyName("authorizing_integration_owners")]
    public Dictionary<string, string> AuthorizingIntegrationOwners { get; set; } = new();

    [JsonPropertyName("context")]
    public int? Context { get; set; }

    [JsonPropertyName("attachment_size_limit")]
    public long AttachmentSizeLimit { get; set; } = 25 * 1024 * 1024;
}

public class InteractionChannelPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }
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

    /// <summary>venta has no native embed/rich-card concept in its message model - these get
    /// flattened into plain text (see DiscordInteractionEndpoint) rather than dropped, since a
    /// LOT of real bots (like most status/health-check commands) reply with embeds and no
    /// `content` at all - silently posting an empty message would look broken with no indication
    /// why.</summary>
    [JsonPropertyName("embeds")]
    public List<EmbedPayload> Embeds { get; set; } = new();

    /// <summary>64 = EPHEMERAL.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}

/// <summary>Subset of Discord's real embed object - just enough to render a readable plain-text
/// fallback (see class remarks on InteractionResponseDataPayload.Embeds).</summary>
public class EmbedPayload
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("author")]
    public EmbedAuthorPayload? Author { get; set; }

    [JsonPropertyName("fields")]
    public List<EmbedFieldPayload> Fields { get; set; } = new();

    [JsonPropertyName("footer")]
    public EmbedFooterPayload? Footer { get; set; }
}

public class EmbedAuthorPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class EmbedFieldPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("inline")]
    public bool Inline { get; set; }
}

public class EmbedFooterPayload
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}
