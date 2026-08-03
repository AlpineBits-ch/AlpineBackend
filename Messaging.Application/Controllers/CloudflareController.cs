using Echo.Realtime.Sfu;
using System.Security.Claims;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;

using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine;

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
    LockedJsonCacheStore callStore,
    IMessageBus bus,
    DeviceIdResolver devices,
    ILogger<CloudflareController> logger) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>See VoiceController.ResolveDeviceAsync - same resolver, same fallback for
    /// pre-update clients, same rejection of an id this user has no device for.</summary>
    private Task<DeviceIdResult> ResolveDeviceAsync(CancellationToken ct = default) =>
        devices.ResolveAsync(Request, UserId, ct);

    /// <summary>Binds a minted Cloudflare session to the user who minted it.</summary>
    private static string SessionOwnerKey(string cfSessionId) => $"cf-session-owner:{cfSessionId}";

    private async Task<bool> OwnsSessionAsync(string? cfSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfSessionId)) return false;

        var owner = await cache.GetStringAsync(SessionOwnerKey(cfSessionId), ct);
        return owner is not null && string.Equals(owner, UserId, StringComparison.Ordinal);
    }

    /// <summary>The caller must be a connected participant of this call.</summary>
    private async Task<bool> IsConnectedParticipantAsync(string callId)
    {
        var call = await LoadCall(callId);
        return call is not null && call.Participants.Any(p => p.UserId == UserId && p.Status == CallStatus.Connected);
    }

    /// <summary>Creates a Cloudflare session for this call participant.</summary>
    /// <param name="primary">Whether this session carries the participant's microphone.</param>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(
        string callId, CancellationToken ct, [FromQuery] bool primary = true)
    {
        var device = await ResolveDeviceAsync(ct);
        if (device.IsUnknown)
            return BadRequest($"Unknown {DeviceIdentity.HeaderName} '{device.DeviceId}' - register the device first.");

        // Only a participant of this call may open a session against it.
        var call0 = await LoadCall(callId);
        if (call0 is null || !call0.IsParticipant(UserId)) return NotFound();

        var cfSessionId = await cfService.CreateSessionAsync(ct);

        // Record ownership before the session is usable, so every later action can verify the
        // caller actually minted it.
        await cache.SetStringAsync(SessionOwnerKey(cfSessionId), UserId, CacheOptions, token: ct);

        if (!primary) return Ok(new { cfSessionId });

        // Locked: this read-modify-write on the Call blob was racing ExchangeParticipantJoined
        // below (fired by the OTHER participant publishing their audio track) whenever both
        // happened close together -e.g. the callee accepting right as the caller finishes
        // publishing.
        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            call =>
            {
                var me = call.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is not null) call.ConnectDevice(me, device.DeviceId);
            }, CacheOptions, ct);

        if (call is not null)
        {
            foreach (var evt in call.GetDomainEvents())
            {
                await bus.PublishAsync(evt);
            }
        }

        // Store reverse mapping so OnDisconnectedAsync can find this user's call
        await cache.SetStringAsync($"user-call:{UserId}", callId, CacheOptions, token: ct);

        return Ok(new { cfSessionId });
    }

    [HttpPost("cf/tracks/new")]
    public async Task<IActionResult> TracksNew(string callId, [FromBody] TracksNewBody body, CancellationToken ct)
    {
        // Both halves matter.
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

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
            // See the identical guard in Guild.Application's GuildCloudflareController: answering a
            // failed subscribe with a 200 is what made this failure mode permanent and invisible.
            logger.LogError(ex,
                "tracks/new failed for user {UserId} in call {CallId} on session {CfSessionId}",
                UserId, callId, body.CfSessionId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.ResponseBody });
        }

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

    [HttpPut("cf/renegotiate")]
    public async Task<IActionResult> Renegotiate(string callId, [FromBody] RenegotiateBody body, CancellationToken ct)
    {
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

        var result = await cfService.RenegotiateAsync(body.CfSessionId,
            new CfRenegotiateRequest(body.SessionDescription), ct);
        return Ok(result);
    }

    [HttpPut("cf/tracks/close")]
    public async Task<IActionResult> CloseTracks(string callId, [FromBody] CloseTracksBody body, CancellationToken ct)
    {
        // Ownership is the load-bearing check here: closing tracks is a hard teardown (force: true),
        // so without it a co-participant could silence any other participant on demand, and the
        // broadcast below would name the attacker rather than the victim - leaving the victim shown
        // as connected and simply inaudible.
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

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

    private Task<Call?> LoadCall(string callId) => callStore.LoadAsync<Call>(Call.GetCacheId(callId));

    private async Task ExchangeParticipantJoined(string callId, string cfSessionId, CancellationToken ct)
    {
        // Locked for the same reason as CreateSession above -this is the write half of the
        // race that silently dropped the caller's CfSessionId/AudioTrackName.
        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c =>
            {
                var me = c.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is not null)
                {
                    me.CfSessionId = cfSessionId;
                    me.AudioTrackName = "audio";
                }
            }, CacheOptions, ct);
        if (call is null) return;

        // The mutation above no-ops for a non-participant, but LockedJsonCacheStore returns the
        // loaded entity regardless of what the mutation did - so execution used to fall through to
        // the disclosure loop below and hand a non-participant every connected participant's
        // CfSessionId. That is exactly the value needed to subscribe to their audio.
        if (call.Participants.All(p => p.UserId != UserId)) return;

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
