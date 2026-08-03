using System.Security.Claims;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

[Authorize]
public class DiscordGuildMemberEndpoint
{
    [WolverineGet("/api/discord/v10/guilds/{guildId}/members")]
    public async Task<IResult> ListMembersAsync(string guildId, int? limit,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        // The caller's identity was previously read and then never used, so this returned the full
        // roster of ANY guild on the instance to any caller holding a valid token. Note the
        // /api/discord/v10/** routes accept an ordinary user JWT as well as a bot token (the
        // translation middleware only rewrites "Bot "-prefixed headers), so this was reachable by
        // every registered account, not just by bots.
        var permission = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(new HasUserPermissionToGuildRequest
        {
            GuildId = guildId,
            UserId = botUserId,
            Permission = ExternalPermission.ViewChannel,
        });
        if (!permission.IsAllowed) return Results.Forbid();

        var response = await bus.InvokeAsync<ListGuildMembersResponse>(new ListGuildMembersRequest
        {
            GuildId = guildId,
            Limit = limit ?? 100,
        });

        var members = response.Members.Select(m => new
        {
            user = new { id = m.UserId, username = m.Nickname ?? m.UserId, bot = m.IsBot },
            nick = m.Nickname,
            roles = m.RoleIds,
            joined_at = m.JoinedAt,
            pending = m.Pending,
        });

        return Results.Ok(members);
    }
}
