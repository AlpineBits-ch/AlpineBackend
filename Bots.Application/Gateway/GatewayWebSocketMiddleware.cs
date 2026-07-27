namespace Bots.Application.Gateway;

/// <summary>
/// Accepts the raw Gateway WebSocket upgrade. Registered in Program.cs only for requests that
/// are already confirmed to be a WebSocket upgrade (see the app.UseWhen predicate there) - plain
/// GETs to the same path fall through to DiscordGatewayEndpoint's REST discovery response, and
/// this middleware sits entirely before UseAuthentication(): Gateway auth isn't HTTP-header-based
/// at all, it happens inside the OP 2 Identify payload, same as real Discord.
/// </summary>
public class GatewayWebSocketMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopeFactory,
        GatewayConnectionRegistry registry, ILogger<GatewayConnection> connectionLogger,
        IHostApplicationLifetime lifetime)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new GatewayConnection(socket, scopeFactory, registry, connectionLogger);
        await connection.RunAsync(lifetime.ApplicationStopping);
    }
}
