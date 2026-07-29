using Echo.Realtime.Sfu;
using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;

using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Messaging.Application.Controllers;

public record TracksNewBody(string CfSessionId, CfSessionDescription SessionDescription, List<CfTrackNew> Tracks);
public record RenegotiateBody(string CfSessionId, CfSessionDescription SessionDescription);
public record CloseTracksBody(string CfSessionId, List<string> TrackNames);


[Authorize]

[ApiController]
[Route("api/v1/voice/calls/{callId}")]
public class CloudflareController(
    CloudflareService cfService,
    IHubContext<EchoRealtimeHub> hub,
    IDistributedCache cache,
    ILogger<CloudflareController> logger) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(string callId, CancellationToken ct)
    {
        var cfSessionId = await cfService.CreateSessionAsync(ct);
        
        
        var raw = await cache.GetStringAsync(Call.GetCacheId(callId), token: ct);
        if (raw is not null)
        {
            var call = JsonSerializer.Deserialize<Call>(raw)!;
            var me = call.Participants.FirstOrDefault(p => p.UserId == UserId);
            if (me is not null && me.Status != CallStatus.Connected)
            {
                me.Status = CallStatus.Connected;
                await cache.SetStringAsync(Call.GetCacheId(callId),
                    JsonSerializer.Serialize(call), CacheOptions, token: ct);
            }
        }

        // Store reverse mapping so OnDisconnectedAsync can find this user's call
        await cache.SetStringAsync($"user-call:{UserId}", callId, CacheOptions, token: ct);

        return Ok(new { cfSessionId });
    }

    [HttpPost("cf/tracks/new")]
    public async Task<IActionResult> TracksNew(string callId, [FromBody] TracksNewBody body, CancellationToken ct)
    {
        var request = new CfTracksNewRequest(body.SessionDescription, body.Tracks);
        var result = body.Tracks.All(t => t.Location == "remote")
            ? await TracksNewWithRetryAsync(body.CfSessionId, request, ct)
            : await cfService.TracksNewAsync(body.CfSessionId, request, ct);

        var audioTrack = body.Tracks.FirstOrDefault(t => t is { Location: "local", TrackName: "audio" });
        if (audioTrack is not null)
            await ExchangeParticipantJoined(callId, body.CfSessionId, ct);

        var nonAudioLocalTracks = body.Tracks
            .Where(t => t.Location == "local" && t.TrackName != "audio")
            .ToList();
        if (nonAudioLocalTracks.Count > 0)
            await EmitTrackPublished(callId, body.CfSessionId, nonAudioLocalTracks, ct);

        return Ok(result);
    }

    /// <summary>
    /// Subscribing to a track another participant only just published can race Cloudflare's own SFU
    /// eventual consistency — their publish (a separate, concurrent tracks/new call) doesn't always
    /// finish propagating on Cloudflare's side by the time our ParticipantJoined- triggered
    /// subscribe lands.
    /// </summary>
    private async Task<CfTracksNewResponse> TracksNewWithRetryAsync(
        string cfSessionId, CfTracksNewRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await cfService.TracksNewAsync(cfSessionId, request, ct);
            }
            catch (CloudflareCallsException ex) when (attempt < 4)
            {
                logger.LogWarning(
                    "Subscribe tracks/new attempt {Attempt} failed for session {CfSessionId}: {Message}",
                    attempt, cfSessionId, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }
    }

    [HttpPut("cf/renegotiate")]
    public async Task<IActionResult> Renegotiate(string callId, [FromBody] RenegotiateBody body, CancellationToken ct)
    {
        var result = await cfService.RenegotiateAsync(body.CfSessionId,
            new CfRenegotiateRequest(body.SessionDescription), ct);
        return Ok(result);
    }

    [HttpPut("cf/tracks/close")]
    public async Task<IActionResult> CloseTracks(string callId, [FromBody] CloseTracksBody body, CancellationToken ct)
    {
        await cfService.CloseTracksAsync(body.CfSessionId, body.TrackNames, ct);
        
        var call = await LoadCall(callId);
        if (call is not null)
        {
            var otherIds = call.Participants
                .Where(p => p.UserId != UserId && p.Status == CallStatus.Connected)
                .Select(p => p.UserId)
                .ToList();

            var tasks = body.TrackNames
                .Select(tn =>
                {
                    var isScreenAudio = tn.StartsWith("screen-audio-");
                    var isScreen = !isScreenAudio && tn.StartsWith("screen-");
                    var shareId = isScreen ? tn["screen-".Length..]
                        : isScreenAudio ? tn["screen-audio-".Length..]
                        : (string?)null;
                    return hub.Clients.Users(otherIds).SendAsync("call.TrackClosed",
                        new { userId = UserId, trackName = tn, shareId }, ct);
                });
            await Task.WhenAll(tasks);
        }

        return NoContent();
    }

    private async Task<Call?> LoadCall(string callId)
    {
        var raw = await cache.GetStringAsync(Call.GetCacheId(callId));
        return raw is null ? null : JsonSerializer.Deserialize<Call>(raw);
    }

    private async Task SaveCall(Call call)
    {
        await cache.SetStringAsync(
            Call.GetCacheId(call.Id),
            JsonSerializer.Serialize(call),
            CacheOptions);
    }

    private async Task ExchangeParticipantJoined(string callId, string cfSessionId, CancellationToken ct)
    {
        var call = await LoadCall(callId);
        if (call is null) return;

        var me = call.Participants.FirstOrDefault(p => p.UserId == UserId);
        if (me is not null)
        {
            me.CfSessionId = cfSessionId;
            me.AudioTrackName = "audio";
            await SaveCall(call);
        }

        var connectedOthers = call.Participants
            .Where(p => p.UserId != UserId && p.Status == CallStatus.Connected)
            .ToList();

        var joinedPayload = new { userId = UserId, cfSessionId, audioTrackName = "audio" };
        var tasks = connectedOthers
            .Select(p => hub.Clients.User(p.UserId).SendAsync("call.ParticipantJoined", joinedPayload, ct))
            .ToList();

        // Send existing participants back to the joiner
        tasks.AddRange(connectedOthers
            .Where(p => p.CfSessionId is not null)
            .Select(p => hub.Clients.User(UserId).SendAsync("call.ParticipantJoined", new
            {
                userId = p.UserId,
                cfSessionId = p.CfSessionId,
                audioTrackName = p.AudioTrackName ?? "audio",
            }, ct)));

        await Task.WhenAll(tasks);
    }

    private async Task EmitTrackPublished(string callId, string cfSessionId, List<CfTrackNew> tracks, CancellationToken ct)
    {
        var call = await LoadCall(callId);
        if (call is null) return;

        var otherIds = call.Participants
            .Where(p => p.UserId != UserId && p.Status == CallStatus.Connected)
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
            return hub.Clients.Users(otherIds).SendAsync("call.TrackPublished",
                new { userId = UserId, cfSessionId, trackName, kind, shareId }, ct);
        });

        await Task.WhenAll(tasks);
    }
}
