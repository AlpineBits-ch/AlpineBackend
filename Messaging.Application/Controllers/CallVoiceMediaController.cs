using System.Security.Claims;
using AppEnvironment;
using Echo.Entitlements.Wire;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
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

/// <param name="TrackNames">
/// What the caller has just started publishing, in the naming <see cref="TrackNaming"/> defines.
/// </param>
/// <param name="Video">What the caller intends to send on the video tracks.</param>
public record CallPublishBody(List<string> TrackNames, VoiceVideoIntent? Video = null);

public record CallUnpublishBody(List<string> TrackNames);

/// <summary>
/// The SFU-facing half of direct calls: what a client needs to connect, and what it tells us once
/// it has.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/voice/calls/{callId}")]
public class CallVoiceMediaController(
    IVoiceSfu sfu,
    IDistributedCache cache,
    LockedJsonCacheStore callStore,
    IMessageBus bus,
    DeviceIdResolver devices,
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

    /// <summary>Identical to the guild side on purpose - a client implements this once for both room
    /// kinds, which is the whole reason the media contract is shared.</summary>
    private ObjectResult NotConfigured() =>
        StatusCode(503, new { error = "voiceNotConfigured", action = "contactOperator" });

    private ObjectResult Unavailable() =>
        StatusCode(503, new { error = "sfuUnavailable", action = "retry" });

    /// <summary>The caller must be a connected participant.</summary>
    private async Task<bool> IsConnectedParticipantAsync(string callId)
    {
        var call = await LoadCall(callId);
        return call is not null && call.Participants.Any(p => p.UserId == UserId && p.Status == CallStatus.Connected);
    }

    /// <summary>
    /// Everything the caller needs to open its own connection to the SFU, plus the roster work that
    /// used to ride on minting a media session.
    /// </summary>
    /// <param name="primary">Whether this connection carries the participant's microphone.</param>
    [HttpPost("connection")]
    public async Task<IActionResult> CreateConnection(
        string callId, CancellationToken ct,
        [FromQuery] bool primary = true, [FromQuery] string? tag = null)
    {
        if (!sfu.IsConfigured) return NotConfigured();

        var device = await ResolveDeviceAsync(ct);
        if (device.IsUnknown)
            return BadRequest($"Unknown {DeviceIdentity.HeaderName} '{device.DeviceId}' - register the device first.");

        var call0 = await LoadCall(callId);
        if (call0 is null || !call0.IsParticipant(UserId)) return NotFound();

        var identity = primary
            ? VoiceIdentity.Primary(UserId)
            : VoiceIdentity.Secondary(UserId, tag);

        // A call has no permission model of its own beyond participation, so a participant may send
        // everything the operator's ceiling allows.
        var rights = await ResolveRightsAsync(callId, ct);

        VoiceConnection connection;
        try
        {
            connection = await sfu.ConnectAsync(
                Room(callId), identity, displayName: null, rights, maxParticipants: null, ct);
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.Unavailable)
        {
            logger.LogWarning(
                "Could not reach the SFU control plane for call {CallId}: {Detail}", callId, ex.Detail);
            return Unavailable();
        }
        catch (VoiceMediaException ex)
        {
            logger.LogError(ex, "SFU refused a connection for user {UserId} in call {CallId}",
                UserId, callId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.Detail });
        }

        if (!primary) return Ok(Describe(connection, rights));

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

        // No ManageGuild lookup, and not because it was forgotten: a call has no guild, so the only
        // cause a capacity limit can carry here is an operator ceiling, which no amount of money
        // moves and which therefore has no remedy to be permitted or refused.
        var degradations = admission.Describe(
            Env.License.IsHosted && Env.License.IsBillingConfigured, actorCanManageGuild: false);

        var payload = Describe(connection, rights);

        return degradations.Count == 0
            ? Ok(payload)
            : Ok(EntitlementResponses.WithDegradations(payload, degradations));
    }

    /// <summary>"I am still here." Nothing more - see the remarks.</summary>
    [HttpPost("alive")]
    public async Task<IActionResult> Alive(string callId, CancellationToken ct)
    {
        var participant = (await rooms.LoadAsync(Room(callId), ct))?.Find(UserId);
        if (participant is null) return NotFound();

        var device = await ResolveDeviceAsync(ct);
        if (!IsVoiceDevice(participant.DeviceId, CallingDeviceId(device))) return Conflict();

        await VoiceReconciler.ClaimLivenessAsync(cache, UserId, Room(callId), ct);
        return NoContent();
    }

    /// <summary>
    /// The caller's device id as <see cref="IsVoiceDevice"/> wants it: null when they named no
    /// device at all.
    /// </summary>
    private static string? CallingDeviceId(DeviceIdResult device) =>
        device.WasProvided ? device.DeviceId : null;

    /// <summary>
    /// Whether <paramref name="callingDeviceId"/> is the device that holds this room's voice
    /// connection, recorded on the roster as <paramref name="voiceDeviceId"/>.
    /// </summary>
    private static bool IsVoiceDevice(string? voiceDeviceId, string? callingDeviceId) =>
        string.IsNullOrEmpty(voiceDeviceId)
        || string.IsNullOrEmpty(callingDeviceId)
        || voiceDeviceId == callingDeviceId;

    /// <summary>
    /// Records what the caller has published, and re-decides the video ceiling against what they
    /// say they are sending.
    /// </summary>
    [HttpPost("publish")]
    public async Task<IActionResult> Publish(
        string callId, [FromBody] CallPublishBody body, CancellationToken ct)
    {
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();

        var identity = VoiceIdentity.Primary(UserId);
        var audio = body.TrackNames.Where(TrackNaming.IsMicrophone).ToList();
        var video = body.TrackNames.Where(n => !TrackNaming.IsMicrophone(n)).ToList();

        VoicePublishDecision? publish = null;
        IReadOnlyList<EntitlementDegradationDto> degradations = [];

        if (video.Count > 0)
        {
            publish = await voice.EvaluateVideoPublishAsync(
                Room(callId), UserId, VoiceVideoIntent.RequestOf(body.Video), ct);

            // No ManageGuild lookup, and not because it was forgotten: there is no guild here, so a
            // guild-side remedy is not a thing this endpoint can offer.
            var sells = Env.License.IsHosted && Env.License.IsBillingConfigured;

            if (!publish.VideoAllowed)
            {
                logger.LogInformation(
                    "Refusing video publish for user {UserId} in call {CallId}: {Key} bound at {Rung}",
                    UserId, callId, publish.Refusal!.Key.Name, publish.Rung);

                await TryRevokeVideoAsync(callId, identity, ct);

                return StatusCode(
                    EntitlementDenialDto.StatusCode,
                    publish.Denial(sells, actorCanManageGuild: false));
            }

            degradations = publish.Describe(sells, actorCanManageGuild: false);
        }

        if (audio.Count > 0)
            await voice.RecordPublishAsync(Room(callId), UserId, identity, ct);

        if (video.Count > 0)
            await voice.RecordTracksAsync(
                Room(callId), UserId, identity, video, publish?.MaxLayer, ct);

        var result = new
        {
            identity,
            rung = publish?.Rung,
            height = publish?.Height,
            framerate = publish?.Framerate,
            // The wire spelling, not the enum name - same vocabulary as `layer` on a subscription
            // set, for the same reason as the guild side.
            maxLayer = publish?.MaxLayer is { } layer ? VoiceVideoLayers.Name(layer) : null,
        };

        return degradations.Count == 0
            ? Ok(result)
            : Ok(EntitlementResponses.WithDegradations(result, degradations));
    }

    /// <summary>
    /// Re-applies the video ceiling to a publisher who changed what they are sending without
    /// republishing.
    /// </summary>
    [HttpPut("video")]
    public async Task<IActionResult> DeclareVideo(
        string callId, [FromBody] VoiceVideoIntent body, CancellationToken ct)
    {
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();

        var revision = await voice.ReviseVideoLayerAsync(
            Room(callId), UserId, VoiceVideoIntent.RequestOf(body), ct);

        if (revision is { Changed: true, MaxLayer: not null })
            logger.LogInformation(
                "Video ceiling re-applied for user {UserId} in call {CallId}: capped at layer {Layer}",
                UserId, callId, revision.MaxLayer);

        return Ok(new
        {
            changed = revision.Changed,
            maxLayer = revision.MaxLayer is { } layer ? VoiceVideoLayers.Name(layer) : null,
        });
    }

    /// <summary>Records that the caller has stopped publishing tracks, so peers drop them rather than
    /// waiting on media that has ended.</summary>
    [HttpPost("unpublish")]
    public async Task<IActionResult> Unpublish(
        string callId, [FromBody] CallUnpublishBody body, CancellationToken ct)
    {
        if (!await IsConnectedParticipantAsync(callId)) return Forbid();

        await voice.RecordTracksClosedAsync(Room(callId), UserId, body.TrackNames, ct);
        return NoContent();
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

    /// <summary>What this participant may send.</summary>
    private async Task<VoiceMediaRights> ResolveRightsAsync(string callId, CancellationToken ct)
    {
        var decision = await voice.EvaluateVideoPublishAsync(
            Room(callId), UserId, VoiceVideoRequest.Best, ct);

        return decision.VideoAllowed ? VoiceMediaRights.Full : VoiceMediaRights.AudioOnly;
    }

    private async Task TryRevokeVideoAsync(string callId, string identity, CancellationToken ct)
    {
        try
        {
            await sfu.UpdateRightsAsync(Room(callId), identity, VoiceMediaRights.AudioOnly, ct);
        }
        catch (VoiceMediaException ex)
        {
            logger.LogWarning(ex,
                "Could not narrow {Identity}'s publish rights in call {CallId} after refusing their "
                + "video", identity, callId);
        }
    }

    /// <summary>The connection payload.</summary>
    private static object Describe(VoiceConnection connection, VoiceMediaRights rights) => new
    {
        backend = connection.Backend,
        url = connection.Url,
        token = connection.Token,
        room = connection.Room,
        identity = connection.Identity,
        mediaSessionId = connection.Identity,
        expiresAt = connection.ExpiresAt,
        canPublishAudio = rights.MayPublishAudio,
        canPublishVideo = rights.MayPublishVideo,
    };

    private Task<Call?> LoadCall(string callId) => callStore.LoadAsync<Call>(Call.GetCacheId(callId));
}
