using System.Security.Claims;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

/// <summary>
/// Discord-compat REST surface: parse a Discord-shaped request, translate to the existing internal
/// Wolverine bus command/query on the owning service's *.Contracts, map the internal response to a
/// Discord-shaped response.
/// </summary>
[Authorize]
public class DiscordUserEndpoint
{
    [WolverineGet("/api/discord/v10/users/@me")]
    public async Task<IResult> GetSelfAsync([NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var response = await bus.InvokeAsync<GetUserByIdResponse>(new GetUserByIdRequest { UserId = botUserId });
        if (response.User is null) return Results.NotFound();

        return Results.Ok(new
        {
            id = response.User.Id,
            username = response.User.UserName,
            discriminator = "0000",
            bot = true,
            avatar = (string?)null,
        });
    }
}
