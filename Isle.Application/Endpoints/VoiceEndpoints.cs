using System.Security.Claims;
using Isle.Api.Services.Privacy;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Domain.Entity.Voice;
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

/// <summary>A refusal the client can act on without parsing prose.</summary>
public record VoiceRefusalDto(string Code, string Message);

public static class VoiceMembershipEndpoints
{
    /// <summary>The T2-19 refusal code, returned with 403 when the caller has turned
    /// <c>AllowPositionalVoiceCapture</c> off (or it could not be resolved).</summary>
    public const string PositionalVoiceRefusalCode = "positional_voice_consent";

    private static IResult PositionalVoiceRefused() => Results.Json(
        new VoiceRefusalDto(PositionalVoiceRefusalCode,
            "Positional voice capture is turned off for this account. Enable it in privacy settings to join Isle voice."),
        statusCode: StatusCodes.Status403Forbidden);

    [Authorize]
    [WolverinePost("/api/v1/voice/join")]
    public static async Task<IResult> Join([NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db, [NotBody]VoicePlayerRegistry registry,
        [NotBody] PositionalVoiceConsent consent, [NotBody] CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var player = await db.Players
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Id, p.SteamId })
            .FirstOrDefaultAsync(ct);

        if (player is null || string.IsNullOrEmpty(player.SteamId))
            return Results.BadRequest("Player is not fully linked (missing userId/steamId mapping) — cannot join voice.");

        // T2-19. This registration is the whole trigger for positional capture: StatsStreamIngestion
        // ignores position snapshots for anyone it cannot resolve here, so refusing means no cluster
        // membership, no audibility, no SFU position broadcast and no publishable track. Fails closed
        // — an unresolvable setting refuses.
        if (!await consent.MayCaptureAsync(userId, ct))
            return PositionalVoiceRefused();

        // Key the voice grid by userId (the SignalR user identifier) so server->client
        // pushes address the right connection.
        await registry.RegisterAsync(userId, player.SteamId);

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

        await registry.UnregisterAsync(userId);
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
            IsVoiceConnected = registry.IsPlayerOnline(playerId: player.Id),
            IsGameConnected = presenceManager.IsPlayerOnline(player.Id),
        });
        
    }
}