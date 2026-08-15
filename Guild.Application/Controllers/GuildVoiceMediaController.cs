using System.Security.Claims;
using System.Text.Json;
using Echo.Entitlements.Wire;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;

using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Controllers;

/// <param name="Video">
/// What the caller intends to send on the video tracks in <paramref name="Tracks"/>, when there are
/// any.
/// </param>
public record GuildNegotiateBody(
    string MediaSessionId,
    VoiceSessionDescription SessionDescription,
    List<VoiceTrackRef> Tracks,
    VoiceVideoIntent? Video = null);
public record GuildRenegotiateBody(string MediaSessionId, VoiceSessionDescription SessionDescription);
public record GuildCloseTracksBody(string MediaSessionId, List<string> TrackNames);

/// <summary>WebRTC signalling for guild voice channels.</summary>
[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildVoiceMediaController(
    IVoiceMediaTransport media,
    GuildPermissionService permissions,
    ILogger<GuildVoiceMediaController> logger,
    IDistributedCache cache,
    VoiceRoomService voice,
    SfuSessionOwnership sessions) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static VoiceRoomKey Room(string channelId) => VoiceRoomKey.Channel(channelId);

    /// <summary>See <see cref="SfuSessionOwnership"/> - a media session id is a bearer capability
    /// over the whole SFU app, and peers are handed each other's by design, so every action that
    /// acts *as* a session verifies the caller minted it.</summary>
    private Task<bool> OwnsSessionAsync(string? mediaSessionId, CancellationToken ct = default) =>
        sessions.OwnsAsync(mediaSessionId, UserId, ct);

    /// <summary>
    /// The answer to a session whose transport is gone: 409, and the one recovery that works.
    /// </summary>
    private ConflictObjectResult SessionGone() =>
        Conflict(new { error = "sessionGone", action = "recreateSession" });

    /// <summary>Creates a media session for this participant.</summary>
    /// <param name="primary">Whether this session carries the participant's microphone.</param>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(
        string guildId, string channelId, CancellationToken ct, [FromQuery] bool primary = true)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var mediaSessionId = await media.CreateSessionAsync(ct);

        // Recorded before the session is usable, so every later action can verify the caller
        // actually minted it.
        await sessions.BindAsync(mediaSessionId, UserId, ct);

        if (primary)
        {
            await cache.SetStringAsync(
                ChannelVoiceState.GetUserCacheKey(UserId),
                JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId }),
                CacheOptions, ct);
        }

        // The backend is named so a client can pick its transport implementation without inferring
        // it from the shape of the response.
        return Ok(new { mediaSessionId, backend = media.Backend });
    }

    /// <summary>Publishes and/or subscribes tracks.</summary>
    [HttpPost("tracks")]
    public async Task<IActionResult> Negotiate(
        string guildId, string channelId,
        [FromBody] GuildNegotiateBody body,
        CancellationToken ct)
    {
        // Connect gates the whole handler, before the per-track checks below.
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        if (!await OwnsSessionAsync(body.MediaSessionId, ct)) return Forbid();

        var audioTrack = body.Tracks.FirstOrDefault(
            t => t is { Direction: VoiceTrackDirection.Publish, TrackName: TrackNaming.Audio });
        if (audioTrack is not null
            && !await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Speak))
            return Forbid();

        var otherPublishes = body.Tracks
            .Where(t => t.Direction == VoiceTrackDirection.Publish && t.TrackName != TrackNaming.Audio)
            .ToList();
        if (otherPublishes.Count > 0
            && !await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Stream))
            return Forbid();

        // Only a body that actually carries video is measured against the video ceiling.
        VoicePublishDecision? publish = null;
        IReadOnlyList<EntitlementDegradationDto> degradations = [];

        if (otherPublishes.Count > 0)
        {
            publish = await voice.EvaluateVideoPublishAsync(
                Room(channelId), UserId, VoiceVideoIntent.RequestOf(body.Video), ct);

            var sells = GuildVoiceRemedies.InstanceSellsUpgrades;
            var canRemedy = await GuildVoiceRemedies.ActorCanRemedyAsync(
                permissions, UserId, guildId, publish);

            if (!publish.VideoAllowed)
            {
                // The whole request goes, audio included, and that is not an oversight.
                logger.LogInformation(
                    "Refusing video publish for user {UserId} in channel {ChannelId}: {Key} bound at "
                    + "{Rung}", UserId, channelId, publish.Refusal!.Key.Name, publish.Rung);

                return StatusCode(
                    EntitlementDenialDto.StatusCode, publish.Denial(sells, canRemedy));
            }

            degradations = publish.Describe(sells, canRemedy);
        }

        // A subscribe naming media nobody is publishing is a stale client, not a server fault.
        var stale = await voice.FindStaleSubscriptionsAsync(Room(channelId), body.Tracks, ct);
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
        var decision = await voice.PrepareSubscribeAsync(Room(channelId), UserId, body.Tracks, ct);
        if (decision.Unplanned.Count > 0)
        {
            // Answered exactly like a stale subscription, down to the error code: the client is
            // acting on a subscription set it has not caught up with, the recovery is the same
            // refetch, and a second code would be the same sentence written twice in every client.
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
            var missing = await voice.RecordTracksMissingAsync(Room(channelId), blamed, ct);
            logger.LogInformation(
                "Subscribe found media the roster still advertised for user {UserId}: {Detail}. "
                + "Pruned [{Tracks}]; room is now v{Version}",
                UserId, ex.Detail, string.Join(", ", blamed), missing?.Version);
            return Conflict(new { error = "staleSubscription", action = "refetchSnapshot" });
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.SessionGone)
        {
            // The caller's PeerConnection is closed or never connected, so this session id is
            // spent.
            logger.LogInformation(
                "Session {MediaSessionId} has no live transport for user {UserId} in channel "
                + "{ChannelId} - asking them to recreate it: {Detail}",
                body.MediaSessionId, UserId, channelId, ex.Detail);
            return SessionGone();
        }
        catch (VoiceMediaException ex)
        {
            // Must not be answered with a 200. A well-formed response the client cannot distinguish
            // from a working subscribe is what let this failure mode stay invisible.
            logger.LogError(ex,
                "Negotiate failed for user {UserId} in channel {ChannelId} on session {MediaSessionId}",
                UserId, channelId, body.MediaSessionId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.Detail });
        }

        // Recording and announcing are one operation inside the shared service, so the roster can
        // never disagree with what peers were told.
        if (audioTrack is not null)
            await voice.RecordPublishAsync(Room(channelId), UserId, body.MediaSessionId, ct);

        if (otherPublishes.Count > 0)
            await voice.RecordTracksAsync(Room(channelId), UserId, body.MediaSessionId,
                otherPublishes.Select(t => t.TrackName!).ToList(), publish?.MaxLayer, ct);

        // Byte-identical to what a v1 client already receives whenever nothing was reduced, which
        // is every publish inside the guild's plan.
        return degradations.Count == 0
            ? Ok(result)
            : Ok(EntitlementResponses.WithDegradations(result, degradations));
    }

    /// <summary>
    /// What this client can actually see: what it has pinned, what it has collapsed, how large it
    /// draws each tile, and whether it wants a share's audio.
    /// </summary>
    [HttpPost("subscriptions")]
    public async Task<IActionResult> UpdateSubscriber(
        string guildId, string channelId,
        [FromBody] VoiceSubscriberUpdate body,
        CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var plan = await voice.SetSubscriberAsync(Room(channelId), UserId, body, ct);

        return Ok(new
        {
            mode = plan.Mode,
            revision = plan.Revision,
            activeSpeakers = plan.ActiveSpeakers,
            tracks = plan.For(UserId).Tracks,
        });
    }

    [HttpPut("negotiate")]
    public async Task<IActionResult> Renegotiate(
        string guildId, string channelId,
        [FromBody] GuildRenegotiateBody body,
        CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect)) return Forbid();
        if (!await OwnsSessionAsync(body.MediaSessionId, ct)) return Forbid();

        try
        {
            var sdp = await media.RenegotiateAsync(body.MediaSessionId, body.SessionDescription, ct);
            return Ok(new { sessionDescription = sdp });
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.SessionGone)
        {
            // A renegotiation is the likeliest place to meet a dead session: it is what a client
            // sends after its connection state changed.
            logger.LogInformation(
                "Renegotiate on spent session {MediaSessionId} for user {UserId} in channel {ChannelId}: {Detail}",
                body.MediaSessionId, UserId, channelId, ex.Detail);
            return SessionGone();
        }
        catch (VoiceMediaException ex)
        {
            logger.LogError(ex, "Renegotiate failed for user {UserId} in channel {ChannelId}", UserId, channelId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.Detail });
        }
    }

    [HttpPost("tracks/close")]
    public async Task<IActionResult> CloseTracks(
        string guildId, string channelId,
        [FromBody] GuildCloseTracksBody body,
        CancellationToken ct)
    {
        // Ownership is the load-bearing check: closing tracks is a hard teardown, so without it any
        // co-participant - who is handed everyone's session ids by design - could silence a specific
        // victim on demand.
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect)) return Forbid();
        if (!await OwnsSessionAsync(body.MediaSessionId, ct)) return Forbid();

        await media.CloseTracksAsync(body.MediaSessionId, body.TrackNames, ct);

        await voice.RecordTracksClosedAsync(Room(channelId), UserId, body.TrackNames, ct);

        return NoContent();
    }
}
