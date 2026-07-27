using System.Security.Claims;
using System.Text;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

public class DiscordCreateMessageDto
{
    public string Content { get; set; } = "";
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

        var message = await bus.InvokeAsync<Message>(new CreateMessageCommand
        {
            AuthorId = botUserId,
            AuthorIdType = AuthorIdType.Bot,
            Content = Encoding.UTF8.GetBytes(dto.Content),
            ChannelId = channelId,
        });

        return Results.Ok(new
        {
            id = message.Id,
            channel_id = channelId,
            content = dto.Content,
            timestamp = message.CreatedAt,
            author = new { id = botUserId, bot = true },
        });
    }
}
