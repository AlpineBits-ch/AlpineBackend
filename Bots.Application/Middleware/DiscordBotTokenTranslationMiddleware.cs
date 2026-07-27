using Bots.Application.Gateway;

namespace Bots.Application.Middleware;

/// <summary>
/// Bridges Discord's static, long-lived bot tokens onto our short-lived OpenIddict JWTs.
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
