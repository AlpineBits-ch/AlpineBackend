using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
        });
    }

    /// <summary>Discord's `message.edit(...)` - only the bot that authored the message may edit it
    /// (matches Discord's own real rule). Reuses UpdateMessageCommand the exact same way
    /// CreateMessageAsync above reuses CreateMessageCommand - no separate edit logic.</summary>
    [WolverinePatch("/api/discord/v10/channels/{channelId}/messages/{messageId}")]
    public async Task<IResult> EditMessageAsync(string channelId, string messageId, DiscordCreateMessageDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var content = dto.Content.Length > 0 ? dto.Content : EmbedFlattener.Flatten(null, dto.Embeds);

        var result = await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = messageId,
            RequestingAuthorId = botUserId,
            Content = Encoding.UTF8.GetBytes(content),
            EmbedsJson = dto.Embeds.Count > 0 ? JsonSerializer.Serialize(dto.Embeds) : null,
        });

        if (result.NotFound) return Results.NotFound();
        if (result.Forbidden) return Results.Forbid();

        return Results.Ok(new
        {
            id = messageId,
            channel_id = channelId,
            content,
            embeds = dto.Embeds,
            timestamp = result.UpdatedAt,
            author = new { id = botUserId, bot = true },
        });
    }
}
