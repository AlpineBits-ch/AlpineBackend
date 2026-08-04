using System.Security.Claims;
using Echo.Realtime.Sfu;
using Isle.Api.Services.Privacy;
using Isle.Api.Services.State;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Microsoft.AspNetCore.Authorization;
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
///
/// The voice grid is keyed by userId (the SignalR user identifier), which is exactly the
/// JWT NameIdentifier — so no Player lookup is needed here.
/// </summary>
[Authorize]
public class VoiceCloudflareEndpoints
{
    [WolverinePost("/api/v1/voice/cf/session")]
    public async Task<IResult> CreateSession(
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoicePlayerRegistry registry,
        [NotBody] VoiceTrackRegistry tracks,
        [NotBody] PositionalVoiceConsent consent,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        // Ordering guard: a player must opt into voice (register userId<->steamId) before we
        // hand out a Cloudflare session, so position ingestion can cluster them.
        if (!registry.TryGetSteamId(userId, out _))
            return Results.BadRequest("Join voice before opening a Cloudflare session.");

        // T2-19, re-checked rather than inferred from the registry entry. /voice/join is the primary
        // gate, but a registration can outlive the consent that created it — the entry carries a 2h
        // TTL and survives socket drops by design, so a revocation that lands between join and
        // session creation would otherwise be honoured only on the next join.
        if (!await consent.MayCaptureAsync(userId, ct))
            return Results.Forbid();

        var cfSessionId = await cf.CreateSessionAsync(ct);

        // Bind the session to its owner before handing it back. Every endpoint below acts *as* a
        // session, and a session id is a bearer capability over the whole Cloudflare app - so
        // without this, any authenticated player could drive renegotiation on, publish into, or
        // tear down another player's session, given only an id that proximity voice hands out to
        // anyone who comes into earshot.
        tracks.RegisterSession(userId, cfSessionId);

        return Results.Ok(new { cfSessionId });
    }

    [WolverinePost("/api/v1/voice/cf/tracks/new")]
    public async Task<IResult> TracksNew(
        IsleTracksNewBody body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoiceTrackRegistry tracks,
        [NotBody] VoiceCluster cluster,
        [NotBody] ISfuClient sfu,
        [NotBody] PositionalVoiceConsent consent,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (!tracks.OwnsSession(userId, body.CfSessionId))
            return Results.Forbid();

        // T2-19, at the point capture actually begins. A local audio track is the microphone going
        // up: it is recorded in VoiceTrackRegistry and pulled by every audible peer. Checked before
        // anything reaches Cloudflare, and only for the publish direction — subscribing to peers is
        // listening, which this consent does not govern.
        if (body.Tracks.Any(t => t is { Location: "local", TrackName: "audio" })
            && !await consent.MayCaptureAsync(userId, ct))
            return Results.Forbid();

        // Proximity is the entire access-control boundary of positional voice, and it was enforced
        // only by the client honouring isle.PeerLeft: a remote-track pull was relayed to Cloudflare
        // without checking that the peer being pulled is currently audible to the caller. Since a
        // peer's session id is handed over on first earshot and never rotated, one brief encounter
        // bought the ability to keep listening from anywhere on the map, indefinitely. Every remote
        // subscription is now checked against the live audibility graph.
        foreach (var track in body.Tracks.Where(t => t.Location == "remote"))
        {
            var peerId = tracks.ResolveSessionOwner(track.SessionId);
            if (peerId is null) return Results.Forbid();

            // Pulling your own track back is harmless and is how a client re-establishes itself.
            if (peerId == userId) continue;

            if (!cluster.GetAudiblePeers(userId).Contains(peerId)) return Results.Forbid();
        }

        var request = new CfTracksNewRequest(body.SessionDescription, body.Tracks);
        // Subscribes retry, publishes do not - see SubscribeTracksAsync. Isle needs the retry more
        // than the other two callers, not less: VoiceSubscriptionReconcileService marks a pair as
        // pushed once it has sent SubscribeMutual, whatever the client then made of it, so it will
        // not re-drive the pair while both stay audible. A subscribe lost here stays lost until one
        // of them walks out of earshot and back.
        var result = body.Tracks.All(t => t.Location == "remote")
            ? await cf.SubscribeTracksAsync(body.CfSessionId, request, ct)
            : await cf.TracksNewAsync(body.CfSessionId, request, ct);

        // When the player publishes their microphone, record the track so peers can pull it,
        // then (re)subscribe everyone currently sharing their voice cell. Doing this only
        // after the track exists avoids Cloudflare 425s on premature remote pulls.
        var audioTrack = body.Tracks.FirstOrDefault(t => t is { Location: "local", TrackName: "audio" });
        if (audioTrack is not null)
        {
            tracks.Publish(userId, body.CfSessionId, "audio");

            // Seed the (re)connecting client from the warm grid state so a stationary player is
            // placed immediately — no waiting for the next throttled stats snapshot, and no need
            // to move/rejoin. Because a socket drop no longer evicts the player from the cluster
            // (see VoiceUserDisconnectedHandler), GetAudiblePeers/TryGetPosition are already
            // populated here on reconnect.
            if (cluster.TryGetPosition(userId, out var self))
                await sfu.SendSelfPosition(userId, self.X, self.Y, self.Z, self.Yaw,
                    self.Vx, self.Vy, self.Vz, self.TimestampMs);

            foreach (var peer in cluster.GetAudiblePeers(userId).Where(p => p != userId))
            {
                await sfu.SubscribeMutual(userId, peer);

                if (cluster.TryGetPosition(peer, out var pos))
                    await sfu.SendPeerPosition(userId, peer, pos.X, pos.Y, pos.Z, pos.Yaw,
                        pos.Vx, pos.Vy, pos.Vz, pos.TimestampMs);
            }
        }

        return Results.Ok(result);
    }

    [WolverinePut("/api/v1/voice/cf/renegotiate")]
    public async Task<IResult> Renegotiate(
        IsleRenegotiateBody body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoiceTrackRegistry tracks,
        CancellationToken ct)
    {
        // This method did not previously read the caller's identity at all, so any authenticated
        // player could drive SDP renegotiation on any session id they had seen.
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (!tracks.OwnsSession(userId, body.CfSessionId)) return Results.Forbid();

        var result = await cf.RenegotiateAsync(body.CfSessionId,
            new CfRenegotiateRequest(body.SessionDescription), ct);
        return Results.Ok(result);
    }

    [WolverinePut("/api/v1/voice/cf/tracks/close")]
    public async Task<IResult> CloseTracks(
        IsleCloseTracksBody body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] CloudflareService cf,
        [NotBody] VoiceTrackRegistry tracks,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        // Closing tracks is a hard teardown (force: true), so without an ownership check any player
        // holding another's session id could silence them on demand.
        if (!tracks.OwnsSession(userId, body.CfSessionId)) return Results.Forbid();

        await cf.CloseTracksAsync(body.CfSessionId, body.TrackNames, ct);

        if (body.TrackNames.Contains("audio"))
            tracks.Remove(userId);

        return Results.NoContent();
    }
}
