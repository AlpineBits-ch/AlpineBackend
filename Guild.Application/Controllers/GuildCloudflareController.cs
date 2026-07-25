using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;

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
    MicroserviceContext db) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(string guildId, string channelId, CancellationToken ct)
    {
        var canConnect = await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect);
        if (!canConnect) return Forbid();

        var cfSessionId = await cfService.CreateSessionAsync(ct);

        var voiceState = await LoadChannelVoiceStateAsync(channelId, ct);
        if (voiceState is not null)
        {
            var participant = voiceState.Participants.FirstOrDefault(p => p.UserId == UserId);
            if (participant is not null)
            {
                participant.CfSessionId = cfSessionId;
                await SaveChannelVoiceStateAsync(voiceState, ct);
            }
        }

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(UserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId }),
            CacheOptions, ct);

        return Ok(new { cfSessionId });
    }

    [HttpPost("cf/tracks/new")]
    public async Task<IActionResult> TracksNew(
        string guildId, string channelId,
        [FromBody] GuildTracksNewBody body,
        CancellationToken ct)
    {
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

        var result = await cfService.TracksNewAsync(body.CfSessionId,
            new CfTracksNewRequest(body.SessionDescription, body.Tracks), ct);

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
        await cfService.CloseTracksAsync(body.CfSessionId, body.TrackNames, ct);

        var voiceState = await LoadChannelVoiceStateAsync(channelId, ct);
        if (voiceState is not null)
        {
            var me = voiceState.Participants.FirstOrDefault(p => p.UserId == UserId);
            if (me is not null)
            {
                foreach (var tn in body.TrackNames)
                    foreach (var share in me.ActiveScreenShares)
                        share.TrackNames.Remove(tn);
                me.ActiveScreenShares.RemoveAll(s => s.TrackNames.Count == 0);
                await SaveChannelVoiceStateAsync(voiceState, ct);
            }

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

    private async Task<ChannelVoiceState?> LoadChannelVoiceStateAsync(string channelId, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(channelId), ct);
        return raw is null ? null : JsonSerializer.Deserialize<ChannelVoiceState>(raw);
    }

    private async Task SaveChannelVoiceStateAsync(ChannelVoiceState voiceState, CancellationToken ct)
    {
        await cache.SetStringAsync(
            ChannelVoiceState.GetCacheKey(voiceState.ChannelId),
            JsonSerializer.Serialize(voiceState),
            CacheOptions, ct);
    }

    private async Task ExchangeParticipantJoined(string channelId, string cfSessionId, CancellationToken ct)
    {
        var voiceState = await LoadChannelVoiceStateAsync(channelId, ct);
        if (voiceState is null) return;

        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == UserId);
        if (me is not null)
        {
            me.CfSessionId = cfSessionId;
            me.AudioTrackName = "audio";
            await SaveChannelVoiceStateAsync(voiceState, ct);
        }

        var others = voiceState.Participants
            .Where(p => p.UserId != UserId)
            .ToList();

        var joinedPayload = new { userId = UserId, cfSessionId, audioTrackName = "audio", channelId };
        var tasks = others
            .Select(p => hub.Clients.User(p.UserId).SendAsync("guild.voice.ParticipantJoined", joinedPayload, ct))
            .ToList();

        tasks.AddRange(others
            .Where(p => p.CfSessionId is not null)
            .Select(p => hub.Clients.User(UserId).SendAsync("guild.voice.ParticipantJoined", new
            {
                userId = p.UserId,
                cfSessionId = p.CfSessionId,
                audioTrackName = p.AudioTrackName ?? "audio",
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
        var voiceState = await LoadChannelVoiceStateAsync(channelId, ct);
        if (voiceState is null) return;

        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == UserId);
        if (me is not null)
        {
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
            await SaveChannelVoiceStateAsync(voiceState, ct);
        }

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
