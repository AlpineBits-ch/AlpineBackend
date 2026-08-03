using Echo.Realtime.Sfu;
using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;

using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Controllers;

public record GuildTracksNewBody(string CfSessionId, CfSessionDescription SessionDescription, List<CfTrackNew> Tracks);
public record GuildRenegotiateBody(string CfSessionId, CfSessionDescription SessionDescription);
public record GuildCloseTracksBody(string CfSessionId, List<string> TrackNames);

[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildCloudflareController(
    CloudflareService cfService,
    GuildPermissionService permissions,
    IHubContext<EchoRealtimeHub> hub,
    ILogger<GuildCloudflareController> logger,
    IDistributedCache cache,
    LockedJsonCacheStore voiceStore,
    MicroserviceContext db) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Binds a minted Cloudflare session to the user who minted it.</summary>
    private static string SessionOwnerKey(string cfSessionId) => $"guild-cf-session-owner:{cfSessionId}";

    private async Task<bool> OwnsSessionAsync(string? cfSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfSessionId)) return false;

        var owner = await cache.GetStringAsync(SessionOwnerKey(cfSessionId), ct);
        return owner is not null && string.Equals(owner, UserId, StringComparison.Ordinal);
    }

    /// <summary>Creates a Cloudflare session for this participant.</summary>
    /// <param name="primary">Whether this session carries the participant's microphone.</param>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(
        string guildId, string channelId, CancellationToken ct, [FromQuery] bool primary = true)
    {
        var canConnect = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect);
        if (!canConnect) return Forbid();

        var cfSessionId = await cfService.CreateSessionAsync(ct);

        // Recorded before the session is usable, so every later action can verify the caller
        // actually minted it.
        await cache.SetStringAsync(SessionOwnerKey(cfSessionId), UserId, CacheOptions, ct);

        if (primary)
        {
            // Locked: races Join/ExchangeParticipantJoined/CloseTracks for the same channelId -see
            // the comment on GuildVoiceController.Join for the class of bug this prevents.
            await voiceStore.UpdateAsync<ChannelVoiceState>(
                ChannelVoiceState.GetCacheKey(channelId), ChannelVoiceState.GetCacheKey(channelId),
                voiceState =>
                {
                    var participant = voiceState.Participants.FirstOrDefault(p => p.UserId == UserId);
                    if (participant is not null) participant.CfSessionId = cfSessionId;
                }, CacheOptions, ct);

            await cache.SetStringAsync(
                ChannelVoiceState.GetUserCacheKey(UserId),
                JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId }),
                CacheOptions, ct);
        }

        return Ok(new { cfSessionId });
    }

    [HttpPost("cf/tracks/new")]
    public async Task<IActionResult> TracksNew(
        string guildId, string channelId,
        [FromBody] GuildTracksNewBody body,
        CancellationToken ct)
    {
        // Connect gates the whole handler, before the per-track checks below.
        var canConnect = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect);
        if (!canConnect) return Forbid();

        // And the session being acted as must be the caller's own.
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

        var audioTrack = body.Tracks.FirstOrDefault(t => t is { Location: "local", TrackName: "audio" });
        if (audioTrack is not null)
        {
            var canSpeak = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Speak);
            logger.LogInformation("User {UserId} can speak in channel {ChannelId}", UserId, channelId);
            if (!canSpeak) return Forbid();
        }

        var nonAudioLocal = body.Tracks
            .Where(t => t.Location == "local" && t.TrackName != "audio")
            .ToList();
        if (nonAudioLocal.Count > 0)
        {
            var canStream = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Stream);
            logger.LogInformation("User {UserId} can stream in channel {ChannelId}", UserId, channelId);
            if (!canStream) return Forbid();
        }

        var request = new CfTracksNewRequest(body.SessionDescription, body.Tracks);
        CfTracksNewResponse result;
        try
        {
            result = body.Tracks.All(t => t.Location == "remote")
                ? await cfService.SubscribeTracksAsync(body.CfSessionId, request, ct)
                : await cfService.TracksNewAsync(body.CfSessionId, request, ct);
        }
        catch (CloudflareCallsException ex)
        {
            // Must not be answered with a 200. A well-formed response the client can't distinguish
            // from a working subscribe is what let this failure mode stay invisible: the client
            // records the participant as subscribed and every retry path it has is gated behind
            // that same guard, so nothing ever tries again.
            logger.LogError(ex,
                "tracks/new failed for user {UserId} in channel {ChannelId} on session {CfSessionId}",
                UserId, channelId, body.CfSessionId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.ResponseBody });
        }

        if (audioTrack is not null)
            await ExchangeParticipantJoined(channelId, body.CfSessionId, ct);

        if (nonAudioLocal.Count > 0)
            await EmitTrackPublished(channelId, body.CfSessionId, nonAudioLocal, ct);

        return Ok(result);
    }

    [HttpPut("cf/renegotiate")]
    public async Task<IActionResult> Renegotiate(
        string guildId, string channelId,
        [FromBody] GuildRenegotiateBody body,
        CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect)) return Forbid();
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

        var result = await cfService.RenegotiateAsync(body.CfSessionId,
            new CfRenegotiateRequest(body.SessionDescription), ct);
        return Ok(result);
    }

    [HttpPut("cf/tracks/close")]
    public async Task<IActionResult> CloseTracks(
        string guildId, string channelId,
        [FromBody] GuildCloseTracksBody body,
        CancellationToken ct)
    {
        // Ownership is the load-bearing check: CloseTracks is a hard teardown (force: true), so
        // without it any co-participant - who is handed everyone's session ids by design - could
        // silence a specific victim on demand.
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect)) return Forbid();
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

        await cfService.CloseTracksAsync(body.CfSessionId, body.TrackNames, ct);

        // Locked: same class of race as CreateSession/ExchangeParticipantJoined above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(channelId), ChannelVoiceState.GetCacheKey(channelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is null) return;
                foreach (var tn in body.TrackNames)
                    foreach (var share in me.ActiveScreenShares)
                        share.TrackNames.Remove(tn);
                me.ActiveScreenShares.RemoveAll(s => s.TrackNames.Count == 0);
            }, CacheOptions, ct);

        if (voiceState is not null)
        {
            var otherIds = voiceState.Participants
                .Where(p => p.UserId != UserId)
                .Select(p => p.UserId)
                .ToList();

            var tasks = body.TrackNames
                .Select(tn => hub.Clients.Users(otherIds).SendAsync("guild.voice.TrackClosed",
                    new { userId = UserId, trackName = tn, channelId }, ct));
            await Task.WhenAll(tasks);
        }

        return NoContent();
    }

    private async Task ExchangeParticipantJoined(string channelId, string cfSessionId, CancellationToken ct)
    {
        // Locked -this write is the direct counterpart of the bug reported for the 1:1 call
        // flow: it races CreateSession/Join/CloseTracks for the same channelId, and the
        // last-writer-wins overwrite could silently wipe the publisher's own CfSessionId/
        // AudioTrackName right back out, leaving nobody able to subscribe to their audio.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(channelId), ChannelVoiceState.GetCacheKey(channelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is not null)
                {
                    me.CfSessionId = cfSessionId;
                    me.AudioTrackName = "audio";
                }
                else
                {
                    logger.LogWarning(
                        "ExchangeParticipantJoined: publisher {UserId} not found in channel {ChannelId}'s cached "
                        + "participants — their own CfSessionId never got saved", UserId, channelId);
                }
            }, CacheOptions, ct);

        if (voiceState is null)
        {
            logger.LogWarning(
                "ExchangeParticipantJoined: no cached ChannelVoiceState for channel {ChannelId} — {UserId}'s "
                + "publish will notify nobody", channelId, UserId);
            return;
        }

        logger.LogInformation(
            "ExchangeParticipantJoined: channel {ChannelId}, publisher {UserId}, cached participants: {Participants}",
            channelId, UserId,
            string.Join(", ", voiceState.Participants.Select(p => $"{p.UserId}(cfSessionId={p.CfSessionId ?? "null"})")));

        var others = voiceState.Participants
            .Where(p => p.UserId != UserId)
            .ToList();

        logger.LogInformation(
            "ExchangeParticipantJoined: notifying {Count} other participant(s) about {UserId}'s join: {Others}",
            others.Count, UserId, string.Join(", ", others.Select(p => p.UserId)));

        var joinedPayload = new { userId = UserId, cfSessionId, audioTrackName = "audio", channelId };
        var tasks = others
            .Select(p => hub.Clients.User(p.UserId).SendAsync("guild.voice.ParticipantJoined", joinedPayload, ct))
            .ToList();

        // AudioTrackName, not CfSessionId, is what says "this participant has published audio".
        tasks.AddRange(others
            .Where(p => p.CfSessionId is not null && p.AudioTrackName is not null)
            .Select(p => hub.Clients.User(UserId).SendAsync("guild.voice.ParticipantJoined", new
            {
                userId = p.UserId,
                cfSessionId = p.CfSessionId,
                audioTrackName = p.AudioTrackName,
                channelId
            }, ct)));

        // Replay existing screen shares to the new joiner
        foreach (var p in others.Where(p => p.CfSessionId is not null && p.ActiveScreenShares.Count > 0))
        {
            foreach (var share in p.ActiveScreenShares)
            {
                tasks.Add(hub.Clients.User(UserId).SendAsync("guild.voice.ScreenShareStarted", new
                {
                    userId = p.UserId,
                    shareId = share.ShareId,
                    trackName = $"screen-{share.ShareId}",
                    channelId
                }, ct));

                tasks.AddRange(share.TrackNames.Select(trackName =>
                {
                    var isScreenAudio = trackName.StartsWith("screen-audio-");
                    var isScreen = !isScreenAudio && trackName.StartsWith("screen-");
                    var kind = isScreen ? "screen" : isScreenAudio ? "screenAudio" : "video";
                    var shareId = isScreen ? trackName["screen-".Length..]
                        : isScreenAudio ? trackName["screen-audio-".Length..]
                        : null;
                    return hub.Clients.User(UserId).SendAsync("guild.voice.TrackPublished", new
                    {
                        userId = p.UserId,
                        cfSessionId = p.CfSessionId,
                        trackName,
                        kind,
                        shareId,
                        channelId
                    }, ct);
                }));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task EmitTrackPublished(string channelId, string cfSessionId, List<CfTrackNew> tracks, CancellationToken ct)
    {
        // Locked -same class of race as ExchangeParticipantJoined above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(channelId), ChannelVoiceState.GetCacheKey(channelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is null) return;

                foreach (var track in tracks)
                {
                    var tn = track.TrackName!;
                    var isScreenAudio = tn.StartsWith("screen-audio-");
                    var isScreen = !isScreenAudio && tn.StartsWith("screen-");
                    if (!isScreen && !isScreenAudio) continue;

                    var sid = isScreen ? tn["screen-".Length..] : tn["screen-audio-".Length..];
                    var share = me.ActiveScreenShares.FirstOrDefault(s => s.ShareId == sid);
                    if (share is null)
                    {
                        share = new ActiveScreenShare { ShareId = sid };
                        me.ActiveScreenShares.Add(share);
                    }
                    if (!share.TrackNames.Contains(tn))
                        share.TrackNames.Add(tn);
                }
            }, CacheOptions, ct);
        if (voiceState is null) return;

        var otherIds = voiceState.Participants
            .Where(p => p.UserId != UserId)
            .Select(p => p.UserId)
            .ToList();

        var tasks = tracks.Select(track =>
        {
            var trackName = track.TrackName!;
            var isScreenAudio = trackName.StartsWith("screen-audio-");
            var isScreen = !isScreenAudio && trackName.StartsWith("screen-");
            var kind = isScreen ? "screen" : isScreenAudio ? "screenAudio" : "video";
            var shareId = isScreen ? trackName["screen-".Length..]
                : isScreenAudio ? trackName["screen-audio-".Length..]
                : null;
            return hub.Clients.Users(otherIds).SendAsync("guild.voice.TrackPublished",
                new { userId = UserId, cfSessionId, trackName, kind, shareId, channelId }, ct);
        });

        await Task.WhenAll(tasks);
    }
}
