using System.Security.Claims;
using Isle.Api.Services;
using Isle.Api.Voice;
using Isle.Contracts.Commands;
using Isle.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Isle.Api.Endpoints;


public class VoiceConnectionStatusDto
{
    public bool IsGameConnected { get; set; }
    public bool IsVoiceConnected { get; set; }
}
public static class VoiceMembershipEndpoints
{
    [Authorize]
    [WolverinePost("/api/v1/voice/join")]
    public static async Task<IResult> Join(
        HttpContext http, MicroserviceContext db, VoicePlayerRegistry registry, IMessageBus bus, CancellationToken ct)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var player = await db.Players
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Id, p.SteamId })
            .FirstOrDefaultAsync(ct);

        if (player is null || string.IsNullOrEmpty(player.SteamId))
            return Results.BadRequest("Player is not fully linked (missing userId/steamId mapping) — cannot join voice.");

        // Key the voice grid by userId (the SignalR user identifier) so server->client
        // pushes address the right connection.
        registry.Register(userId, player.SteamId);

        // No cluster membership yet — that begins the moment a StatsStream
        // snapshot for this steamId arrives (see PositionIngestionService).
        return Results.NoContent();
    }

    [Authorize]
    [WolverinePost("/api/v1/voice/leave")]
    public static async Task<IResult> Leave(
        [NotBody] VoicePlayerRegistry registry, [NotBody] VoiceTrackRegistry tracks, [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        registry.Unregister(userId);
        tracks.Remove(userId);
        await bus.InvokeAsync(new RemovePlayerCommand(userId));

        return Results.NoContent();
    }


    [Authorize]
    [WolverineGet("/api/v1/voice/status")]
    public static async Task<IResult> GetConnectionStatus( [NotBody] ClaimsPrincipal user, [NotBody] VoicePlayerRegistry registry, MicroserviceContext context, PlayerPresenceManager presenceManager)
    {
        var player = await context.Players.AsNoTracking()
            .Where(p => p.UserId == user.FindFirstValue(ClaimTypes.NameIdentifier)).FirstOrDefaultAsync();


        if(player is null) return Results.NotFound("Player not registered");
        
        
        if (registry.TryGetPlayerId(player.SteamId, out var _))
        {
            return Results.Ok(new VoiceConnectionStatusDto()
            {
                IsVoiceConnected = true,
                IsGameConnected = true,
            });
        }
        
        return Results.Ok(new VoiceConnectionStatusDto()
        {
            IsVoiceConnected = false,
            IsGameConnected = presenceManager.IsPlayerOnline(player.Id),
        });
        
    }
}