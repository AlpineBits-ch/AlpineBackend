using System.Security.Claims;
using System.Text.Json;
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

public record GuildNegotiateBody(string MediaSessionId, VoiceSessionDescription SessionDescription, List<VoiceTrackRef> Tracks);
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

        // A subscribe naming media nobody is publishing is a stale client, not a server fault.
        var stale = await voice.FindStaleSubscriptionsAsync(Room(channelId), body.Tracks, ct);
        if (stale.Count > 0)
        {
            logger.LogInformation(
                "Rejecting stale subscribe for user {UserId}: {Tracks} no longer published",
                UserId, string.Join(", ", stale));
            return Conflict(new { error = "staleSubscription", tracks = stale, action = "refetchSnapshot" });
        }

        var request = new VoiceNegotiateRequest(body.SessionDescription, body.Tracks);
        VoiceNegotiateResponse result;
        try
        {
            result = body.Tracks.All(t => t.Direction == VoiceTrackDirection.Subscribe)
                ? await media.SubscribeAsync(body.MediaSessionId, request, ct)
                : await media.PublishAsync(body.MediaSessionId, request, ct);
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.TrackNotFound)
        {
            // The publisher stopped between the roster check and the pull.
            logger.LogInformation(
                "Subscribe raced a publisher going away for user {UserId}: {Detail}", UserId, ex.Detail);
            return Conflict(new { error = "staleSubscription", action = "refetchSnapshot" });
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
                otherPublishes.Select(t => t.TrackName!).ToList(), ct);

        return Ok(result);
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
