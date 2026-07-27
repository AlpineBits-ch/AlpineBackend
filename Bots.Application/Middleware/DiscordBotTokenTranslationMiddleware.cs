using Bots.Application.Gateway;

namespace Bots.Application.Middleware;

/// <summary>
/// Bridges Discord's static, long-lived bot tokens onto our short-lived OpenIddict JWTs.
/// Registered only in front of the /api/discord/v10 route group, before UseAuthentication(),
/// so it can rewrite the Authorization header before the normal JWT-bearer pipeline ever
/// sees the request - the compat surface and the native surface then share the exact same
/// auth/authorization code path. The actual unpack/exchange/cache logic lives in
/// <see cref="BotTokenTranslator"/>, shared with the Gateway WebSocket's IDENTIFY handler.
/// </summary>
public class DiscordBotTokenTranslationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, BotTokenTranslator translator)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bot ", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var result = await translator.AuthenticateAsync(authHeader["Bot ".Length..]);
        if (!result.Success)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Request.Headers.Authorization = $"Bearer {result.Jwt}";
        await next(context);
    }
}
