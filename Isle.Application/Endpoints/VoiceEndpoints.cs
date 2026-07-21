using System.Security.Claims;
using Isle.Api.Services;
using Isle.Contracts.Commands;
using Isle.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public static class VoiceMembershipEndpoints
{
    [Authorize]
    [WolverinePost("/api/v1/isle/voice/join")]
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

        registry.Register(player.Id, player.SteamId);

        // No cluster membership yet — that begins the moment a StatsStream
        // snapshot for this steamId arrives (see PositionIngestionService).
        return Results.NoContent();
    }

    [Authorize]
    [WolverinePost("/api/v1/isle/voice/leave")]
    public static async Task<IResult> Leave(
        HttpContext http, MicroserviceContext db, VoicePlayerRegistry registry, IMessageBus bus, CancellationToken ct)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var player = await db.Players
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);

        if (player is null) return Results.NoContent();

        registry.Unregister(player.Id);
        await bus.InvokeAsync(new RemovePlayer(player.Id));

        return Results.NoContent();
    }
}