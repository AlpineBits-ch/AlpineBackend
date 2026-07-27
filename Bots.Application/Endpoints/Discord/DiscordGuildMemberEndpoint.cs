using System.Security.Claims;
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
        });

        return Results.Ok(members);
    }
}
