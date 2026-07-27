using System.Text;
using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Bots.Application.Gateway.Handlers;

/// <summary>
/// Translates Guild.Application's re-published MessageCreatedForBots (see that project's
/// MessageCreatedHandler, which resolves the GuildId this needs) into a Gateway MESSAGE_CREATE
/// dispatch for every bot installed in the guild.
/// </summary>
public class MessageCreatedForBotsHandler
{
    public static async Task Handle(MessageCreatedForBots message, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IMessageBus bus)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, message.GuildId);
        if (botUserIds.Count == 0) return;

        var authorResponse = await bus.InvokeAsync<GetUserByIdResponse>(new GetUserByIdRequest { UserId = message.AuthorId });
        var author = new DiscordUserPayload
        {
            Id = message.AuthorId,
            Username = authorResponse.User?.UserName ?? message.AuthorId,
            Bot = authorResponse.User?.IsBot ?? false,
        };

        var content = message.EncryptionState == MessageEncryptionState.Plain
            ? Encoding.UTF8.GetString(message.Content)
            : ""; // encrypted content can't be decoded for a compat client - see class remarks

        var payload = new MessageCreatePayload
        {
            Id = message.MessageId,
            ChannelId = message.ChannelId,
            GuildId = message.GuildId,
            Content = content,
            Author = author,
            Timestamp = DateTimeOffset.UtcNow,
        };

        foreach (var botUserId in botUserIds)
        {
            if (botUserId == message.AuthorId) continue; // don't echo a bot's own message back to it
            await registry.PublishAsync(botUserId, "MESSAGE_CREATE", payload);
        }
    }
}
