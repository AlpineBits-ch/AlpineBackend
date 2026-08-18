using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

public class DiscordCreateMessageDto
{
    public string Content { get; set; } = "";

    /// <summary>Stored structurally on the message (see Message.EmbedsJson) so a client that
    /// understands embeds can render rich cards. See EmbedFlattener for the plain-text fallback
    /// used when Content is otherwise empty, for clients that don't.</summary>
    public List<EmbedPayload> Embeds { get; set; } = new();
}

/// <summary>PATCH body for an edit.</summary>
public class DiscordEditMessageDto
{
    private string? _content;
    private List<EmbedPayload>? _embeds;
    private List<ComponentPayload>? _components;

    [JsonPropertyName("content")]
    public string? Content
    {
        get => _content;
        set { _content = value; HasContent = true; }
    }

    [JsonPropertyName("embeds")]
    public List<EmbedPayload>? Embeds
    {
        get => _embeds;
        set { _embeds = value; HasEmbeds = true; }
    }

    [JsonPropertyName("components")]
    public List<ComponentPayload>? Components
    {
        get => _components;
        set { _components = value; HasComponents = true; }
    }

    private int? _flags;

    /// <summary>Discord's message flags.</summary>
    [JsonPropertyName("flags")]
    public int? Flags
    {
        get => _flags;
        set { _flags = value; HasFlags = true; }
    }

    /// <summary>True when the body carried a `content` key at all - including `"content": null`.</summary>
    [JsonIgnore]
    public bool HasContent { get; private set; }

    [JsonIgnore]
    public bool HasEmbeds { get; private set; }

    [JsonIgnore]
    public bool HasComponents { get; private set; }

    [JsonIgnore]
    public bool HasFlags { get; private set; }
}

[Authorize]
public class DiscordMessageEndpoint
{
    [WolverinePost("/api/discord/v10/channels/{channelId}/messages")]
    public async Task<IResult> CreateMessageAsync(string channelId, DiscordCreateMessageDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(new HasUserPermissionToChannelRequest
        {
            ChannelId = channelId,
            UserId = botUserId,
            Permission = ExternalPermission.SendMessages,
        });
        if (!permission.IsAllowed) return Results.Forbid();

        var content = dto.Content.Length > 0 ? dto.Content : EmbedFlattener.Flatten(null, dto.Embeds);

        var message = await bus.InvokeAsync<Message>(new CreateMessageCommand
        {
            AuthorId = botUserId,
            AuthorIdType = AuthorIdType.Bot,
            Content = Encoding.UTF8.GetBytes(content),
            ChannelId = channelId,
            EmbedsJson = dto.Embeds.Count > 0 ? JsonSerializer.Serialize(dto.Embeds) : null,
        });

        return Results.Ok(new
        {
            id = message.Id,
            channel_id = channelId,
            content,
            embeds = dto.Embeds,
            timestamp = message.CreatedAt,
            author = new { id = botUserId, bot = true },
            author_type = DiscordAuthorType.Bot,
        });
    }

    /// <summary>
    /// Discord's `message.edit(...)` - only the bot that authored the message may edit it (matches
    /// Discord's own real rule).
    /// </summary>
    [WolverinePatch("/api/discord/v10/channels/{channelId}/messages/{messageId}")]
    public async Task<IResult> EditMessageAsync(string channelId, string messageId, DiscordEditMessageDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var result = await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = messageId,
            RequestingAuthorId = botUserId,
            Content = dto.HasContent ? Encoding.UTF8.GetBytes(dto.Content ?? "") : null,
            EmbedsJson = dto.HasEmbeds ? JsonSerializer.Serialize(dto.Embeds ?? []) : null,
            ComponentsJson = dto.HasComponents ? JsonSerializer.Serialize(dto.Components ?? []) : null,
            Flags = dto.HasFlags ? dto.Flags : null,
            // A flags-only edit is not an author edit: suppressing link previews should no more
            // mark the message "(edited)" here than it does when a human does it through the
            // ordinary API. Content changes still count, exactly as before.
            IsAuthorEdit = dto.HasContent,
        });

        if (result.NotFound) return Results.NotFound();
        if (result.Forbidden) return Results.Forbid();

        // Echoed off the stored result, not off the request body - the response has to show the
        // whole message as it now stands, including the fields this edit did not touch.
        return Results.Ok(new
        {
            id = messageId,
            channel_id = channelId,
            content = Encoding.UTF8.GetString(result.Content ?? []),
            embeds = DeserializeEmbeds(result.EmbedsJson),
            timestamp = result.UpdatedAt,
            author = new { id = botUserId, bot = true },
            author_type = DiscordAuthorType.Bot,
        });
    }

    private static List<EmbedPayload> DeserializeEmbeds(string? embedsJson) =>
        string.IsNullOrWhiteSpace(embedsJson)
            ? []
            : JsonSerializer.Deserialize<List<EmbedPayload>>(embedsJson) ?? [];
}
