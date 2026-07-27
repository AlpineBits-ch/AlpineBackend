namespace Bots.Application.Gateway;

/// <summary>Accepts the raw Gateway WebSocket upgrade.</summary>
public class GatewayWebSocketMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopeFactory,
        GatewayConnectionRegistry registry, ILogger<GatewayConnection> connectionLogger,
        ILogger<GatewayWebSocketMiddleware> logger, IHostApplicationLifetime lifetime)
    {
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var connection = new GatewayConnection(socket, scopeFactory, registry, connectionLogger);
            await connection.RunAsync(lifetime.ApplicationStopping);
        }
        catch (Exception ex)
        {
            // Covers failures before GatewayConnection.RunAsync's own try/catch even starts -
            // e.g. AcceptWebSocketAsync itself failing - so nothing here dies silently either.
            logger.LogError(ex, "Failed to accept/handle a Gateway WebSocket connection");
        }
    }
}
