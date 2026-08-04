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

    /// <summary>Discord's interaction type. 2 = APPLICATION_COMMAND (a slash command),
    /// 3 = MESSAGE_COMPONENT (a button click or select), 4 = APPLICATION_COMMAND_AUTOCOMPLETE,
    /// 5 = MODAL_SUBMIT. Defaults to 2 because that was the only type this layer produced
    /// originally and every existing caller relies on the default.</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = InteractionType.ApplicationCommand;

    /// <summary>Present on MESSAGE_COMPONENT interactions: the message whose component was used.
    /// discord.js reads it to build `interaction.message`, which is what an "update this message
    /// in place" handler operates on.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InteractionMessagePayload? Message { get; set; }

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

/// <summary>Discord's interaction type numbers.</summary>
public static class InteractionType
{
    public const int ApplicationCommand = 2;
    public const int MessageComponent = 3;
    public const int Autocomplete = 4;
    public const int ModalSubmit = 5;
}

/// <summary>Discord's interaction *callback* type numbers - what a bot sends back.</summary>
public static class InteractionCallbackType
{
    public const int ChannelMessageWithSource = 4;
    public const int DeferredChannelMessageWithSource = 5;
    public const int DeferredUpdateMessage = 6;
    public const int UpdateMessage = 7;
    public const int AutocompleteResult = 8;
    public const int Modal = 9;
}

/// <summary>The subset of a message discord.js needs to construct `interaction.message`.</summary>
public class InteractionMessagePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("components")]
    public List<ComponentPayload> Components { get; set; } = new();
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

    // ── MESSAGE_COMPONENT / MODAL_SUBMIT ─────────────────────────────────────

    /// <summary>Which component was used (type 3) or which modal was submitted (type 5).</summary>
    [JsonPropertyName("custom_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomId { get; set; }

    /// <summary>The used component's own type - lets a bot distinguish a button from a select
    /// without re-deriving it from custom_id.</summary>
    [JsonPropertyName("component_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ComponentType { get; set; }

    /// <summary>Selected values, for the select components. Empty for a button.</summary>
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Values { get; set; }

    /// <summary>Submitted modal rows, each an ActionRow wrapping one filled-in TextInput.</summary>
    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComponentPayload>? Components { get; set; }
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
    /// <summary>On UPDATE_MESSAGE this is a patch, so "was the key there at all" is the question
    /// that matters - see <see cref="HasContent"/>.</summary>
    [JsonPropertyName("content")]
    public string? Content
    {
        get => _content;
        set { _content = value; HasContent = true; }
    }

    /// <summary>venta has no native embed/rich-card concept in its message model - these get
    /// flattened into plain text (see DiscordInteractionEndpoint) rather than dropped, since a
    /// LOT of real bots (like most status/health-check commands) reply with embeds and no
    /// `content` at all - silently posting an empty message would look broken with no indication
    /// why.</summary>
    [JsonPropertyName("embeds")]
    public List<EmbedPayload> Embeds
    {
        // An explicit `"embeds": null` normalises to an empty list rather than null: on the update
        // path it means "clear them", and every create-path reader can then stay null-oblivious.
        get => _embeds;
        set { _embeds = value ?? new(); HasEmbeds = true; }
    }

    private string? _content;
    private List<EmbedPayload> _embeds = new();

    /// <summary>Whether the body carried a `content` key at all. On an UPDATE_MESSAGE an absent
    /// field means "leave it as it is" - the overwhelmingly common component response only sends
    /// `components` (to disable the buttons it just handled), and treating that as "content: empty,
    /// embeds: none" blanked the message it was updating.</summary>
    [JsonIgnore]
    public bool HasContent { get; private set; }

    /// <summary>Whether the body carried an `embeds` key at all - see <see cref="HasContent"/>.</summary>
    [JsonIgnore]
    public bool HasEmbeds { get; private set; }

    /// <summary>64 = EPHEMERAL, and now enforced: the response is delivered over the realtime hub
    /// to the invoking user alone and never written to the message store, so it is absent from
    /// history and gone on reload. That matches Discord's own semantics, where an ephemeral reply
    /// is not a message anyone can scroll back to.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    /// <summary>Interactive components to attach. Carried opaquely - see ComponentPayload.</summary>
    [JsonPropertyName("components")]
    public List<ComponentPayload> Components { get; set; } = new();

    // ── Modal (callback type 9) ──────────────────────────────────────────────

    /// <summary>Modal identifier, echoed back on submit as the MODAL_SUBMIT custom_id.</summary>
    [JsonPropertyName("custom_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomId { get; set; }

    /// <summary>Modal window title.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>Autocomplete choices (callback type 8).</summary>
    [JsonPropertyName("choices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AutocompleteChoicePayload>? Choices { get; set; }
}

public class AutocompleteChoicePayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Nullable, not a bare JsonElement: a default(JsonElement) has ValueKind.Undefined,
    /// and System.Text.Json throws InvalidOperationException when asked to write one. A bot that
    /// omits `value` on a choice would otherwise take down the whole callback rather than losing
    /// one suggestion. Null is simply omitted from the serialized choice.</summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Value { get; set; }
}

/// <summary>Subset of Discord's real embed object - just enough to render a readable plain-text
/// fallback (see class remarks on InteractionResponseDataPayload.Embeds).</summary>
/// <summary>
/// Discord's embed object. Two producers write this shape: a bot posting a rich card, and the
/// Unfurl service turning a posted link into a preview (docs/specs/message-previews.md). Both
/// serialize into the same opaque <c>Message.EmbedsJson</c> column, so there is exactly one
/// definition and it lives here.
///
/// <para>Every string is clamped to Discord's documented limit by <c>EmbedLimits</c> before it is
/// persisted, and the summed length across all embeds on a message is capped at 6000 - an unfurled
/// page is attacker-authored text and would otherwise decide how large a message row is.</para>
/// </summary>
public class EmbedPayload
{
    /// <summary>
    /// <c>rich</c> (bot-authored, the default), <c>link</c>, <c>article</c>, <c>image</c>,
    /// <c>video</c>, or <c>gifv</c>. Clients pick a layout from this; <c>video</c>/<c>gifv</c> is
    /// what turns a card into an in-app player.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = EmbedTypes.Rich;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Content timestamp (ISO-8601), e.g. an article's publication date. Not the time the
    /// embed was generated.</summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// Accent colour. Kept as a string rather than Discord's integer because this field predates
    /// the unfurler and bots already send <c>"#5865F2"</c> here; <see cref="EmbedColor"/> converts.
    /// </summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("author")]
    public EmbedAuthorPayload? Author { get; set; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbedProviderPayload? Provider { get; set; }

    /// <summary>Large hero image, rendered full-width below the text.</summary>
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbedMediaPayload? Image { get; set; }

    /// <summary>Small image, rendered beside the text. Also the poster frame for a
    /// <see cref="Video"/> embed.</summary>
    [JsonPropertyName("thumbnail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbedMediaPayload? Thumbnail { get; set; }

    /// <summary>
    /// The in-app player. <b>Only the Unfurl service's provider registry may populate this</b>, and
    /// only with a URL from its whitelist - a client renders this in an iframe, so generic Open
    /// Graph parsing must never be able to reach it. See
    /// <c>Unfurl.Application/Providers/ProviderRegistry.cs</c> and the architecture test that
    /// enforces it.
    /// </summary>
    [JsonPropertyName("video")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbedMediaPayload? Video { get; set; }

    [JsonPropertyName("fields")]
    public List<EmbedFieldPayload> Fields { get; set; } = new();

    [JsonPropertyName("footer")]
    public EmbedFooterPayload? Footer { get; set; }

    /// <summary>Bitfield - see <see cref="EmbedFlags"/>.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    /// <summary>Whether this embed was generated by the unfurler rather than written by an author.
    /// Determines whether a re-unfurl may replace it.</summary>
    [JsonIgnore]
    public bool IsGenerated => (Flags & EmbedFlags.ServerGenerated) != 0;
}

public static class EmbedTypes
{
    public const string Rich = "rich";
    public const string Link = "link";
    public const string Article = "article";
    public const string Image = "image";
    public const string Video = "video";
    public const string Gifv = "gifv";
}

public static class EmbedFlags
{
    /// <summary>Discord's own allocation - an embed that is a content-inventory entry.</summary>
    public const int IsContentInventoryEntry = 1 << 5;

    /// <summary>
    /// venta extension: this embed was produced by the Unfurl service from a link in the message,
    /// not supplied by the message's author.
    ///
    /// <para>Deliberately a flag bit rather than a <c>_generated</c> JSON key: a Discord-compatible
    /// client round-trips <c>flags</c> as an integer it does not interpret, whereas an unknown
    /// object key is routinely dropped by strict deserializers - and losing this bit would let a
    /// re-unfurl overwrite a bot's hand-written card. Bit 16 is far clear of Discord's allocations
    /// (highest documented is 1 &lt;&lt; 5).</para>
    /// </summary>
    public const int ServerGenerated = 1 << 16;
}

/// <summary>Image, thumbnail or video payload. Same shape for all three, as Discord does it.</summary>
public class EmbedMediaPayload
{
    /// <summary>The origin's own URL. Clients should prefer <see cref="ProxyUrl"/>; this is kept so
    /// "open original" works and so a proxy outage degrades to hot-linking rather than to nothing.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>Our re-hosted copy (<c>/api/v1/previews/media/{hash}</c>). Present only once the
    /// media has actually been fetched and stored. Rendering this instead of <see cref="Url"/> is
    /// what keeps a viewer's IP address off the third-party origin.</summary>
    [JsonPropertyName("proxy_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProxyUrl { get; set; }

    /// <summary>True pixel dimensions, measured by decoding the image - never taken from
    /// <c>og:image:width</c>, which is attacker-supplied and frequently wrong. Null when the media
    /// was not fetched. Clients need these to reserve layout space before the image loads.</summary>
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    [JsonPropertyName("content_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; set; }

    /// <summary>Base64 thumbhash - a handful of bytes that decode to a blurred preview, shown while
    /// the real image loads. Same field and purpose as Discord's.</summary>
    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; set; }

    /// <summary>Which encoding <see cref="Placeholder"/> uses, so the algorithm can change without
    /// breaking clients holding old messages. Always 1 today.</summary>
    [JsonPropertyName("placeholder_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlaceholderVersion { get; set; }
}

public class EmbedProviderPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }
}

public class EmbedAuthorPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("icon_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconUrl { get; set; }

    [JsonPropertyName("proxy_icon_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProxyIconUrl { get; set; }
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

    [JsonPropertyName("icon_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconUrl { get; set; }

    [JsonPropertyName("proxy_icon_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProxyIconUrl { get; set; }
}
