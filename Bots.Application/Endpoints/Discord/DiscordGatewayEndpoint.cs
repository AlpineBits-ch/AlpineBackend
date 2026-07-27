using Bots.Application.Gateway;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

/// <summary>Discord's public, unauthenticated gateway-discovery endpoint. Kept for parity even
/// though real bot libraries almost always call the authenticated /gateway/bot below instead.</summary>
public class DiscordGatewayEndpoint
{
    [WolverineGet("/api/discord/v10/gateway")]
    public IResult GetGateway() => Results.Ok(new { url = GatewayUrl.Build() });
}

/// <summary>The endpoint real bot libraries call before connecting - a bot's own token
/// authenticates the request, same DiscordBotTokenTranslationMiddleware shim as every other
/// /api/discord/v10 route. shards/session_start_limit are static, generous values - no real
/// enforcement is needed for the "normal, not huge" bot scope this compat layer targets.</summary>
[Authorize]
public class DiscordGatewayBotEndpoint
{
    [WolverineGet("/api/discord/v10/gateway/bot")]
    public IResult GetGatewayBot() => Results.Ok(new
    {
        url = GatewayUrl.Build(),
        shards = 1,
        session_start_limit = new
        {
            total = 1000,
            remaining = 1000,
            reset_after = 0,
            max_concurrency = 1,
        },
    });
}
