using Echo.Realtime.Sfu;
using System.Security.Claims;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Tracks;

using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Messaging.Application.Controllers;

public record TracksNewBody(string CfSessionId, CfSessionDescription SessionDescription, List<CfTrackNew> Tracks);
public record RenegotiateBody(string CfSessionId, CfSessionDescription SessionDescription);
public record CloseTracksBody(string CfSessionId, List<string> TrackNames);

/// <summary>The SFU signalling relay for direct calls.</summary>
[Authorize]
[ApiController]
[Route("api/v1/voice/calls/{callId}")]
public class CloudflareController(
    CloudflareService cfService,
    IDistributedCache cache,
    LockedJsonCacheStore callStore,
    IMessageBus bus,
    DeviceIdResolver devices,
    SfuSessionOwnership sessions,
    VoiceRoomService voice,
    VoiceRoomStore rooms,
    ILogger<CloudflareController> logger) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static VoiceRoomKey Room(string callId) => VoiceRoomKey.Call(callId);

    private Task<DeviceIdResult> ResolveDeviceAsync(CancellationToken ct = default) =>
        devices.ResolveAsync(Request, UserId, ct);

    /// <summary>See <see cref="SfuSessionOwnership"/> - a session id is a bearer capability over
    /// the whole Cloudflare app, and peers are handed each other's by design, so every action that
    /// acts *as* a session verifies the caller minted it.</summary>
    private Task<bool> OwnsSessionAsync(string? cfSessionId, CancellationToken ct = default) =>
        sessions.OwnsAsync(cfSessionId, UserId, ct);

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
        await sessions.BindAsync(cfSessionId, UserId, ct);

        if (!primary) return Ok(new { cfSessionId });

        // The media state of any device this connection supersedes lives in the room, not on the
        // aggregate, so it is read here and handed to the domain for the takeover event.
        var existingRoom = await rooms.LoadAsync(Room(callId), ct);
        var superseded = existingRoom?.Find(UserId);

        // This is also how the *caller* becomes Connected - they never Accept their own call - so
        // it goes through Call.ConnectDevice, same as Accept, for identical takeover detection.
        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c =>
            {
                var me = c.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is not null)
                    c.ConnectDevice(me, device.DeviceId, superseded?.CfSessionId, superseded?.AudioTrackName);
            }, CacheOptions, ct);

        if (call is not null)
        {
            foreach (var evt in call.GetDomainEvents())
                await bus.PublishAsync(evt);
        }

        // Roster first, media later - and the joiner is handed a full snapshot in the same step.
        await voice.JoinAsync(Room(callId), UserId, device.DeviceId, guildId: null, ct);

        // Reverse index so a disconnect can find this user's call.
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
            // Answering a failed subscribe with a 200 is what made this failure mode permanent and
            // invisible - the client records the participant as subscribed and never retries.
            logger.LogError(ex,
                "tracks/new failed for user {UserId} in call {CallId} on session {CfSessionId}",
                UserId, callId, body.CfSessionId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.ResponseBody });
        }

        var audioTrack = body.Tracks.FirstOrDefault(t => t is { Location: "local", TrackName: TrackNaming.Audio });
        if (audioTrack is not null)
            await voice.RecordPublishAsync(Room(callId), UserId, body.CfSessionId, ct);

        var nonAudioLocalTracks = body.Tracks
            .Where(t => t.Location == "local" && t.TrackName != TrackNaming.Audio)
            .ToList();
        if (nonAudioLocalTracks.Count > 0)
            await voice.RecordTracksAsync(Room(callId), UserId, body.CfSessionId,
                nonAudioLocalTracks.Select(t => t.TrackName!).ToList(), ct);

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
        // broadcast would name the attacker rather than the victim.
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

        await cfService.CloseTracksAsync(body.CfSessionId, body.TrackNames, ct);

        await voice.RecordTracksClosedAsync(Room(callId), UserId, body.TrackNames, ct);

        return NoContent();
    }

    private Task<Call?> LoadCall(string callId) => callStore.LoadAsync<Call>(Call.GetCacheId(callId));
}
