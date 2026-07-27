namespace Bots.Application.Gateway;

/// <summary>Accepts the raw Gateway WebSocket upgrade.</summary>
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
