using System.Security.Claims;
using Isle.Api.Services;
using Isle.Api.Voice;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

// Request bodies for the Cloudflare Calls signalling relay. The server never
// terminates media — it only proxies SDP negotiation to Cloudflare and records
// enough (session id + track name) for peers to pull each other's voice.
public record IsleTracksNewBody(string CfSessionId, CfSessionDescription SessionDescription, List<CfTrackNew> Tracks);
public record IsleRenegotiateBody(string CfSessionId, CfSessionDescription SessionDescription);
public record IsleCloseTracksBody(string CfSessionId, List<string> TrackNames);

/// <summary>
/// Client-facing WebRTC signalling for Isle proximity voice. The game client drives its
/// own peer connection; these endpoints forward each step to Cloudflare via
/// <see cref="CloudflareService"/> and, once a player publishes their microphone, register
/// the track and subscribe their current voice-cell roommates.
/// </summary>
[Authorize]
public class VoiceCloudflareEndpoints
{
    [WolverinePost("/api/v1/isle/voice/cf/session")]
    public async Task<IResult> CreateSession(
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoicePlayerRegistry registry,
        [NotBody] MicroserviceContext db,
        CancellationToken ct)
    {
        var playerId = await ResolvePlayerId(user, db, ct);
        if (playerId is null) return Results.Unauthorized();

        // Ordering guard: a player must opt into voice (register steamId<->playerId)
        // before we hand out a Cloudflare session, so position ingestion can cluster them.
        if (!registry.TryGetSteamId(playerId, out _))
            return Results.BadRequest("Join voice before opening a Cloudflare session.");

        var cfSessionId = await cf.CreateSessionAsync(ct);
        return Results.Ok(new { cfSessionId });
    }

    [WolverinePost("/api/v1/isle/voice/cf/tracks/new")]
    public async Task<IResult> TracksNew(
        IsleTracksNewBody body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoiceTrackRegistry tracks,
        [NotBody] VoiceCluster cluster,
        [NotBody] ISfuClient sfu,
        [NotBody] MicroserviceContext db,
        CancellationToken ct)
    {
        var playerId = await ResolvePlayerId(user, db, ct);
        if (playerId is null) return Results.Unauthorized();

        var result = await cf.TracksNewAsync(body.CfSessionId,
            new CfTracksNewRequest(body.SessionDescription, body.Tracks), ct);

        // When the player publishes their microphone, record the track so peers can pull it,
        // then (re)subscribe everyone currently sharing their voice cell. Doing this only
        // after the track exists avoids Cloudflare 425s on premature remote pulls.
        var audioTrack = body.Tracks.FirstOrDefault(t => t is { Location: "local", TrackName: "audio" });
        if (audioTrack is not null)
        {
            tracks.Publish(playerId, body.CfSessionId, "audio");

            foreach (var roommate in cluster.GetRoommates(playerId).Where(r => r != playerId))
                await sfu.SubscribeMutual(playerId, roommate);
        }

        return Results.Ok(result);
    }

    [WolverinePut("/api/v1/isle/voice/cf/renegotiate")]
    public async Task<IResult> Renegotiate(
        IsleRenegotiateBody body,
        [NotBody] CloudflareService cf,
        CancellationToken ct)
    {
        var result = await cf.RenegotiateAsync(body.CfSessionId,
            new CfRenegotiateRequest(body.SessionDescription), ct);
        return Results.Ok(result);
    }

    [WolverinePut("/api/v1/isle/voice/cf/tracks/close")]
    public async Task<IResult> CloseTracks(
        IsleCloseTracksBody body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoiceTrackRegistry tracks,
        [NotBody] MicroserviceContext db,
        CancellationToken ct)
    {
        var playerId = await ResolvePlayerId(user, db, ct);
        if (playerId is null) return Results.Unauthorized();

        await cf.CloseTracksAsync(body.CfSessionId, body.TrackNames, ct);

        if (body.TrackNames.Contains("audio"))
            tracks.Remove(playerId);

        return Results.NoContent();
    }

    private static async Task<string?> ResolvePlayerId(ClaimsPrincipal user, MicroserviceContext db, CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return null;

        return await db.Players.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }
}
