using System.Security.Claims;
using AppEnvironment;
using Echo.Entitlements.Wire;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;

using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Messaging.Application.Controllers;

/// <param name="Video">
/// What the caller intends to send on the video tracks in <paramref name="Tracks"/>, when there are
/// any.
/// </param>
public record NegotiateBody(
    string MediaSessionId,
    VoiceSessionDescription SessionDescription,
    List<VoiceTrackRef> Tracks,
    VoiceVideoIntent? Video = null);
public record RenegotiateBody(string MediaSessionId, VoiceSessionDescription SessionDescription);
public record CloseTracksBody(string MediaSessionId, List<string> TrackNames);

/// <summary>WebRTC signalling for direct calls.</summary>
[Authorize]
[ApiController]
[Route("api/v1/voice/calls/{callId}")]
public class CallVoiceMediaController(
    IVoiceMediaTransport media,
    IDistributedCache cache,
    LockedJsonCacheStore callStore,
    IMessageBus bus,
    DeviceIdResolver devices,
    SfuSessionOwnership sessions,
    VoiceRoomService voice,
    VoiceRoomStore rooms,
    ILogger<CallVoiceMediaController> logger) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static VoiceRoomKey Room(string callId) => VoiceRoomKey.Call(callId);

    private Task<DeviceIdResult> ResolveDeviceAsync(CancellationToken ct = default) =>
        devices.ResolveAsync(Request, UserId, ct);

    private Task<bool> OwnsSessionAsync(string? mediaSessionId, CancellationToken ct = default) =>
        sessions.OwnsAsync(mediaSessionId, UserId, ct);

    /// <summary>
    /// The answer to a session whose transport is gone: 409, and the one recovery that works.
    /// </summary>
    private ConflictObjectResult SessionGone() =>
        Conflict(new { error = "sessionGone", action = "recreateSession" });

    /// <summary>The caller must be a connected participant.</summary>
    private async Task<bool> IsConnectedParticipantAsync(string callId)
    {
        var call = await LoadCall(callId);
        return call is not null && call.Participants.Any(p => p.UserId == UserId && p.Status == CallStatus.Connected);
    }

    /// <summary>Creates a media session for this call participant.</summary>
    /// <param name="primary">Whether this session carries the participant's microphone.</param>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(
        string callId, CancellationToken ct, [FromQuery] bool primary = true)
    {
        var device = await ResolveDeviceAsync(ct);
        if (device.IsUnknown)
            return BadRequest($"Unknown {DeviceIdentity.HeaderName} '{device.DeviceId}' - register the device first.");

        var call0 = await LoadCall(callId);
        if (call0 is null || !call0.IsParticipant(UserId)) return NotFound();

        var mediaSessionId = await media.CreateSessionAsync(ct);
        await sessions.BindAsync(mediaSessionId, UserId, ct);

        if (!primary) return Ok(new { mediaSessionId, backend = media.Backend });

        // The media state of any device this connection supersedes lives in the room, so it is read
        // here and handed to the domain for the takeover event.
        var existingRoom = await rooms.LoadAsync(Room(callId), ct);
        var superseded = existingRoom?.Find(UserId);

        // This is also how the *caller* becomes Connected - they never Accept their own call.
        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c =>
            {
                var me = c.Participants.FirstOrDefault(p => p.UserId == UserId);
                if (me is not null)
                    c.ConnectDevice(me, device.DeviceId, superseded?.MediaSessionId, superseded?.AudioTrackName);
            }, CacheOptions, ct);

        if (call is not null)
        {
            foreach (var evt in call.GetDomainEvents())
                await bus.PublishAsync(evt);
        }

        // Roster first, media later - and the joiner is handed a full snapshot in the same step.
        var admission = await voice.AdmitAsync(Room(callId), UserId, device.DeviceId, guildId: null, ct);

        // Liveness is claimed here, not left to the first heartbeat.
        await cache.SetStringAsync(
            VoiceReconciler.LivenessKey(UserId), Room(callId).ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VoiceReconciler.LivenessTtl },
            ct);

        await cache.SetStringAsync($"user-call:{UserId}", callId, CacheOptions, token: ct);

        var session = new { mediaSessionId, backend = media.Backend };

        // No ManageGuild lookup, and not because it was forgotten: a call has no guild, so the only
        // cause a capacity limit can carry here is an operator ceiling, which no amount of money
        // moves and which therefore has no remedy to be permitted or refused.
        var degradations = admission.Describe(
            Env.License.IsHosted && Env.License.IsBillingConfigured, actorCanManageGuild: false);

        return degradations.Count == 0
            ? Ok(session)
            : Ok(EntitlementResponses.WithDegradations(session, degradations));
    }

    /// <summary>Publishes and/or subscribes tracks.</summary>
    [HttpPost("tracks")]
    public async Task<IActionResult> Negotiate(string callId, [FromBody] NegotiateBody body, CancellationToken ct)
    {
        // Both halves matter.
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.MediaSessionId, ct)) return Forbid();

        var audioTrack = body.Tracks.FirstOrDefault(
            t => t is { Direction: VoiceTrackDirection.Publish, TrackName: TrackNaming.Audio });

        var otherPublishes = body.Tracks
            .Where(t => t.Direction == VoiceTrackDirection.Publish && t.TrackName != TrackNaming.Audio)
            .ToList();

        // Only a body that actually carries video is measured against the video ceiling.
        VoicePublishDecision? publish = null;
        IReadOnlyList<EntitlementDegradationDto> degradations = [];

        if (otherPublishes.Count > 0)
        {
            publish = await voice.EvaluateVideoPublishAsync(
                Room(callId), UserId, VoiceVideoIntent.RequestOf(body.Video), ct);

            // No ManageGuild lookup, and not because it was forgotten: there is no guild here, so a
            // guild-side remedy is not a thing this endpoint can offer.
            var sells = Env.License.IsHosted && Env.License.IsBillingConfigured;

            if (!publish.VideoAllowed)
            {
                // Refused whole, audio included.
                logger.LogInformation(
                    "Refusing video publish for user {UserId} in call {CallId}: {Key} bound at {Rung}",
                    UserId, callId, publish.Refusal!.Key.Name, publish.Rung);

                return StatusCode(
                    EntitlementDenialDto.StatusCode,
                    publish.Denial(sells, actorCanManageGuild: false));
            }

            degradations = publish.Describe(sells, actorCanManageGuild: false);
        }

        // A subscribe naming media nobody is publishing is a stale client, not a server fault.
        var stale = await voice.FindStaleSubscriptionsAsync(Room(callId), body.Tracks, ct);
        if (stale.Count > 0)
        {
            logger.LogInformation(
                "Rejecting stale subscribe for user {UserId}: {Tracks} no longer published",
                UserId, string.Join(", ", stale));
            return Conflict(new { error = "staleSubscription", tracks = stale, action = "refetchSnapshot" });
        }

        // The plan, consulted once for both of the things it decides: whether this caller is
        // pulling something its subscription set does not include, and which simulcast layer each
        // track it may pull should be served at.
        var decision = await voice.PrepareSubscribeAsync(Room(callId), UserId, body.Tracks, ct);
        if (decision.Unplanned.Count > 0)
        {
            logger.LogInformation(
                "Refusing unplanned subscribe for user {UserId}: {Tracks} are not in their set",
                UserId, string.Join(", ", decision.Unplanned));
            return Conflict(new
            {
                error = "staleSubscription",
                reason = "unplannedSubscription",
                tracks = decision.Unplanned,
                action = "refetchSnapshot",
            });
        }

        var request = new VoiceNegotiateRequest(body.SessionDescription, decision.Tracks);
        VoiceNegotiateResponse result;
        try
        {
            result = body.Tracks.All(t => t.Direction == VoiceTrackDirection.Subscribe)
                ? await media.SubscribeAsync(body.MediaSessionId, request, ct)
                : await media.PublishAsync(body.MediaSessionId, request, ct);
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.TrackNotFound)
        {
            // The roster said this track exists - it passed the pre-check above - and the SFU says
            // it does not.
            var blamed = VoiceRoomService.AttributableSubscribes(body.Tracks);
            var missing = await voice.RecordTracksMissingAsync(Room(callId), blamed, ct);
            logger.LogInformation(
                "Subscribe found media the roster still advertised for user {UserId}: {Detail}. "
                + "Pruned [{Tracks}]; room is now v{Version}",
                UserId, ex.Detail, string.Join(", ", blamed), missing?.Version);
            return Conflict(new { error = "staleSubscription", action = "refetchSnapshot" });
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.SessionGone)
        {
            // The caller's PeerConnection is closed or never connected, so this session id is spent.
            logger.LogInformation(
                "Session {MediaSessionId} has no live transport for user {UserId} in call {CallId} "
                + "- asking them to recreate it: {Detail}",
                body.MediaSessionId, UserId, callId, ex.Detail);
            return SessionGone();
        }
        catch (VoiceMediaException ex)
        {
            logger.LogError(ex,
                "Negotiate failed for user {UserId} in call {CallId} on session {MediaSessionId}",
                UserId, callId, body.MediaSessionId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.Detail });
        }

        if (audioTrack is not null)
            await voice.RecordPublishAsync(Room(callId), UserId, body.MediaSessionId, ct);

        if (otherPublishes.Count > 0)
            await voice.RecordTracksAsync(Room(callId), UserId, body.MediaSessionId,
                otherPublishes.Select(t => t.TrackName!).ToList(), publish?.MaxLayer, ct);

        return degradations.Count == 0
            ? Ok(result)
            : Ok(EntitlementResponses.WithDegradations(result, degradations));
    }

    /// <summary>
    /// What this client can actually see - pins, collapsed tiles, rendered tile sizes, and whether
    /// it wants a share's audio.
    /// </summary>
    [HttpPost("subscriptions")]
    public async Task<IActionResult> UpdateSubscriber(
        string callId, [FromBody] VoiceSubscriberUpdate body, CancellationToken ct)
    {
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();

        var plan = await voice.SetSubscriberAsync(Room(callId), UserId, body, ct);

        return Ok(new
        {
            mode = plan.Mode,
            revision = plan.Revision,
            activeSpeakers = plan.ActiveSpeakers,
            tracks = plan.For(UserId).Tracks,
        });
    }

    [HttpPut("negotiate")]
    public async Task<IActionResult> Renegotiate(string callId, [FromBody] RenegotiateBody body, CancellationToken ct)
    {
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.MediaSessionId, ct)) return Forbid();

        try
        {
            var sdp = await media.RenegotiateAsync(body.MediaSessionId, body.SessionDescription, ct);
            return Ok(new { sessionDescription = sdp });
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.SessionGone)
        {
            logger.LogInformation(
                "Renegotiate on spent session {MediaSessionId} for user {UserId} in call {CallId}: {Detail}",
                body.MediaSessionId, UserId, callId, ex.Detail);
            return SessionGone();
        }
        catch (VoiceMediaException ex)
        {
            logger.LogError(ex, "Renegotiate failed for user {UserId} in call {CallId}", UserId, callId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.Detail });
        }
    }

    [HttpPost("tracks/close")]
    public async Task<IActionResult> CloseTracks(string callId, [FromBody] CloseTracksBody body, CancellationToken ct)
    {
        // Ownership is the load-bearing check here: closing tracks is a hard teardown, so without it
        // a co-participant could silence any other participant on demand.
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();
        if (!await OwnsSessionAsync(body.MediaSessionId, ct)) return Forbid();

        await media.CloseTracksAsync(body.MediaSessionId, body.TrackNames, ct);

        await voice.RecordTracksClosedAsync(Room(callId), UserId, body.TrackNames, ct);

        return NoContent();
    }

    private Task<Call?> LoadCall(string callId) => callStore.LoadAsync<Call>(Call.GetCacheId(callId));
}
