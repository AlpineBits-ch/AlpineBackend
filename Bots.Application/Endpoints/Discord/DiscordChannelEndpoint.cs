using System.Security.Claims;
using Bots.Application.Gateway;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

/// <summary>
/// GET /channels/{id} - real bot libraries call this to explicitly re-fetch/verify a channel
/// (e.g. discord.js's ChannelManager.fetch, used whenever a channel isn't already cached from
/// GUILD_CREATE - a config-supplied channel id checked at startup being the common case) rather
/// than only ever trusting the Gateway hydration cache.
/// </summary>
[Authorize]
public class DiscordChannelEndpoint
{
    [WolverineGet("/api/discord/v10/channels/{channelId}")]
    public async Task<IResult> GetChannelAsync(string channelId, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var response = await bus.InvokeAsync<GetChannelResponse>(new GetChannelRequest { ChannelId = channelId });
        if (response.Channel is null) return Results.NotFound();

        var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(new HasUserPermissionToChannelRequest
        {
            ChannelId = channelId,
            UserId = botUserId,
            Permission = ExternalPermission.ViewChannel,
        });
        if (!permission.IsAllowed) return Results.Forbid();

        var channel = response.Channel;
        return Results.Ok(new
        {
            id = channel.Id,
            type = DiscordChannelType.FromEnumName(channel.Type),
            guild_id = channel.GuildId,
            name = channel.Name,
            position = channel.Position,
            parent_id = channel.CategoryId,
        });
    }
}
