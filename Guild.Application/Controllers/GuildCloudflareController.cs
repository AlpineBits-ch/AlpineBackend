using Echo.Realtime.Sfu;
using System.Security.Claims;
using System.Text.Json;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Tracks;

using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Controllers;

public record GuildTracksNewBody(string CfSessionId, CfSessionDescription SessionDescription, List<CfTrackNew> Tracks);
public record GuildRenegotiateBody(string CfSessionId, CfSessionDescription SessionDescription);
public record GuildCloseTracksBody(string CfSessionId, List<string> TrackNames);

/// <summary>The SFU signalling relay for guild voice channels.</summary>
[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildCloudflareController(
    CloudflareService cfService,
    GuildPermissionService permissions,
    ILogger<GuildCloudflareController> logger,
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

    /// <summary>See <see cref="SfuSessionOwnership"/> - a session id is a bearer capability over
    /// the whole Cloudflare app, and peers are handed each other's by design, so every action that
    /// acts *as* a session verifies the caller minted it.</summary>
    private Task<bool> OwnsSessionAsync(string? cfSessionId, CancellationToken ct = default) =>
        sessions.OwnsAsync(cfSessionId, UserId, ct);

    /// <summary>Creates a Cloudflare session for this participant.</summary>
    /// <param name="primary">Whether this session carries the participant's microphone.</param>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(
        string guildId, string channelId, CancellationToken ct, [FromQuery] bool primary = true)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var cfSessionId = await cfService.CreateSessionAsync(ct);

        // Recorded before the session is usable, so every later action can verify the caller
        // actually minted it.
        await sessions.BindAsync(cfSessionId, UserId, ct);

        if (primary)
        {
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
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        if (!await OwnsSessionAsync(body.CfSessionId, ct)) return Forbid();

        var audioTrack = body.Tracks.FirstOrDefault(t => t is { Location: "local", TrackName: TrackNaming.Audio });
        if (audioTrack is not null
            && !await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Speak))
            return Forbid();

        var nonAudioLocal = body.Tracks
            .Where(t => t.Location == "local" && t.TrackName != TrackNaming.Audio)
            .ToList();
        if (nonAudioLocal.Count > 0
            && !await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Stream))
            return Forbid();

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
            // Must not be answered with a 200. A well-formed response the client cannot distinguish
            // from a working subscribe is what let this failure mode stay invisible.
            logger.LogError(ex,
                "tracks/new failed for user {UserId} in channel {ChannelId} on session {CfSessionId}",
                UserId, channelId, body.CfSessionId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.ResponseBody });
        }

        // Recording and announcing are one operation inside the shared service, so the roster can
        // never disagree with what peers were told.
        if (audioTrack is not null)
            await voice.RecordPublishAsync(Room(channelId), UserId, body.CfSessionId, ct);

        if (nonAudioLocal.Count > 0)
            await voice.RecordTracksAsync(Room(channelId), UserId, body.CfSessionId,
                nonAudioLocal.Select(t => t.TrackName!).ToList(), ct);

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

        await voice.RecordTracksClosedAsync(Room(channelId), UserId, body.TrackNames, ct);

        return NoContent();
    }
}
