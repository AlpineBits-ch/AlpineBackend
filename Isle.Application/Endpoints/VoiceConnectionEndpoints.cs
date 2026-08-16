using System.Security.Claims;
using Echo.Realtime.LiveKit;
using Isle.Api.Services.Privacy;
using Isle.Api.Services.State;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Sfu;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

/// <summary>What the game client needs to connect to proximity voice.</summary>
[Authorize]
public class VoiceConnectionEndpoints
{
    /// <param name="Url">The node's public signalling URL.</param>
    /// <param name="TrackName">
    /// The name the client must publish its microphone under, so that peers and the server agree on
    /// what a player's audio is called.
    /// </param>
    public record VoiceConnectionDto(
        string Url, string Token, string Room, string Identity, string TrackName,
        DateTimeOffset ExpiresAt);

    /// <summary>The microphone track name proximity voice uses.</summary>
    public const string MicrophoneTrackName = "audio";

    [WolverinePost("/api/v1/voice/connection")]
    public async Task<IResult> CreateConnection(
        [NotBody] ClaimsPrincipal user,
        [NotBody] IsleVoiceRoom room,
        [NotBody] LiveKitOptions options,
        [NotBody] VoicePlayerRegistry registry,
        [NotBody] VoiceCluster cluster,
        [NotBody] ISfuClient sfu,
        [NotBody] PositionalVoiceConsent consent,
        [NotBody] ILogger<VoiceConnectionEndpoints> logger,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (!room.IsConfigured)
            return Results.Json(
                new { error = "voiceNotConfigured", action = "contactOperator" }, statusCode: 503);

        // Ordering guard: a player must opt into voice (register userId<->steamId) before we hand out
        // a connection, so position ingestion can cluster them.
        if (!registry.TryGetSteamId(userId, out _))
            return Results.BadRequest("Join voice before opening a connection.");

        // T2-19, re-checked rather than inferred from the registry entry. /voice/join is the primary
        // gate, but a registration can outlive the consent that created it - the entry carries a 2h
        // TTL and survives socket drops by design, so a revocation that lands between join and
        // connection would otherwise be honoured only on the next join.
        if (!await consent.MayCaptureAsync(userId, ct))
            return Results.Forbid();

        LiveKitNode node;
        try
        {
            node = await room.EnsureAsync(ct);
        }
        catch (LiveKitControlException ex)
        {
            // The overlay is down.
            logger.LogWarning(ex, "Could not place the proximity room {Room}", room.Name);
            return Results.Json(new { error = "sfuUnavailable", action = "retry" }, statusCode: 503);
        }

        // Microphone only, and no data channel.
        var grants = new LiveKitGrants(
            CanPublish: true,
            CanSubscribe: true,
            CanPublishData: false,
            CanPublishSources: LiveKitSources.AudioOnly);

        var token = LiveKitToken.ForJoin(
            options, room.Name, userId, displayName: null, grants);

        // Recorded immediately rather than waiting for the next poll, so a player who publishes and
        // then stands still is audible to their neighbours within a push rather than within an
        // interval.
        if (cluster.TryGetPosition(userId, out var self))
            await sfu.SendSelfPosition(userId, self.X, self.Y, self.Z, self.Yaw,
                self.Vx, self.Vy, self.Vz, self.TimestampMs);

        return Results.Ok(new VoiceConnectionDto(
            node.SignalingUrl, token, room.Name, userId, MicrophoneTrackName,
            DateTimeOffset.UtcNow.Add(options.JoinTokenTtl)));
    }
}
