using System.Security.Claims;
using System.Text.Json;
using Echo.Entitlements.Wire;
using Echo.Voice.Rooms;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;

using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Controllers;

/// <param name="TrackNames">What the caller has just started publishing, in the naming
/// <see cref="TrackNaming"/> defines - the same names the client sets on the LiveKit publication, so
/// the roster and the SFU describe the same tracks.</param>
/// <param name="Video">What the caller intends to send on the video tracks, when there are any.
/// Optional and additive: absent is <see cref="VoiceVideoRequest.Best"/>, which resolves to the
/// ceiling rather than to nothing. Ignored for an audio-only body - the video ceiling has never had
/// anything to say about a microphone.</param>
public record GuildPublishBody(List<string> TrackNames, VoiceVideoIntent? Video = null);

public record GuildUnpublishBody(List<string> TrackNames);

/// <summary>
/// The SFU-facing half of guild voice: what a client needs to connect, and what it tells us once it
/// has.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildVoiceMediaController(
    IVoiceSfu sfu,
    GuildPermissionService permissions,
    ILogger<GuildVoiceMediaController> logger,
    IDistributedCache cache,
    VoiceRoomService voice) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static VoiceRoomKey Room(string channelId) => VoiceRoomKey.Channel(channelId);

    /// <summary>The answer when this instance has no SFU: 503 with a reason, never a 500.</summary>
    private ObjectResult NotConfigured() =>
        StatusCode(503, new { error = "voiceNotConfigured", action = "contactOperator" });

    /// <summary>The answer when the control plane could not be reached: 503, and retry.</summary>
    private ObjectResult Unavailable() =>
        StatusCode(503, new { error = "sfuUnavailable", action = "retry" });

    /// <summary>
    /// Everything the caller needs to open its own connection to the SFU: a node URL and a token
    /// naming this room, this participant and these rights.
    /// </summary>
    /// <param name="primary">Whether this connection carries the participant's microphone.</param>
    [HttpPost("connection")]
    public async Task<IActionResult> CreateConnection(
        string guildId, string channelId, CancellationToken ct,
        [FromQuery] bool primary = true, [FromQuery] string? tag = null)
    {
        if (!sfu.IsConfigured) return NotConfigured();

        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var rights = await ResolveRightsAsync(guildId, channelId, ct);

        VoiceConnection connection;
        try
        {
            connection = await sfu.ConnectAsync(
                Room(channelId),
                primary ? VoiceIdentity.Primary(UserId) : VoiceIdentity.Secondary(UserId, tag),
                displayName: null,
                rights,
                maxParticipants: null,
                ct);
        }
        catch (VoiceMediaException ex) when (ex.Failure == VoiceMediaFailure.Unavailable)
        {
            logger.LogWarning(
                "Could not reach the SFU control plane for channel {ChannelId}: {Detail}",
                channelId, ex.Detail);
            return Unavailable();
        }
        catch (VoiceMediaException ex)
        {
            logger.LogError(ex, "SFU refused a connection for user {UserId} in channel {ChannelId}",
                UserId, channelId);
            return StatusCode(502, new { operation = ex.Operation, error = ex.Detail });
        }

        if (primary)
        {
            await cache.SetStringAsync(
                ChannelVoiceState.GetUserCacheKey(UserId),
                JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId }),
                CacheOptions, ct);
        }

        return Ok(Describe(connection, rights));
    }

    /// <summary>
    /// Records what the caller has published, and re-decides the video ceiling against what they
    /// say they are sending.
    /// </summary>
    [HttpPost("publish")]
    public async Task<IActionResult> Publish(
        string guildId, string channelId, [FromBody] GuildPublishBody body, CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var audio = body.TrackNames.Where(TrackNaming.IsMicrophone).ToList();
        if (audio.Count > 0
            && !await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Speak))
            return Forbid();

        var video = body.TrackNames.Where(n => !TrackNaming.IsMicrophone(n)).ToList();
        if (video.Count > 0
            && !await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Stream))
            return Forbid();

        // The participant's identity at the SFU is their user id, and it is what the roster records
        // as the handle peers address them by.
        var identity = VoiceIdentity.Primary(UserId);

        VoicePublishDecision? publish = null;
        IReadOnlyList<EntitlementDegradationDto> degradations = [];

        if (video.Count > 0)
        {
            publish = await voice.EvaluateVideoPublishAsync(
                Room(channelId), UserId, VoiceVideoIntent.RequestOf(body.Video), ct);

            var sells = GuildVoiceRemedies.InstanceSellsUpgrades;
            var canRemedy = await GuildVoiceRemedies.ActorCanRemedyAsync(
                permissions, UserId, guildId, publish);

            if (!publish.VideoAllowed)
            {
                logger.LogInformation(
                    "Refusing video publish for user {UserId} in channel {ChannelId}: {Key} bound at "
                    + "{Rung}", UserId, channelId, publish.Refusal!.Key.Name, publish.Rung);

                // Belt as well as braces.
                await TryRevokeVideoAsync(channelId, identity, ct);

                return StatusCode(
                    EntitlementDenialDto.StatusCode, publish.Denial(sells, canRemedy));
            }

            degradations = publish.Describe(sells, canRemedy);
        }

        if (audio.Count > 0)
            await voice.RecordPublishAsync(Room(channelId), UserId, identity, ct);

        if (video.Count > 0)
            await voice.RecordTracksAsync(
                Room(channelId), UserId, identity, video, publish?.MaxLayer, ct);

        var result = new
        {
            identity,
            rung = publish?.Rung,
            height = publish?.Height,
            framerate = publish?.Framerate,
            maxLayer = publish?.MaxLayer?.ToString(),
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
        string guildId, string channelId, [FromBody] VoiceVideoIntent body, CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var revision = await voice.ReviseVideoLayerAsync(
            Room(channelId), UserId, VoiceVideoIntent.RequestOf(body), ct);

        // The one thing this server can see of a publisher working around their rung: they declared
        // a size at publish time and are now declaring a larger one.
        if (revision is { Changed: true, MaxLayer: not null })
            logger.LogInformation(
                "Video ceiling re-applied for user {UserId} in channel {ChannelId}: capped at layer "
                + "{Layer}", UserId, channelId, revision.MaxLayer);

        return Ok(new { changed = revision.Changed, maxLayer = revision.MaxLayer?.ToString() });
    }

    /// <summary>Records that the caller has stopped publishing tracks, so peers drop them rather than
    /// waiting on media that has ended.</summary>
    [HttpPost("unpublish")]
    public async Task<IActionResult> Unpublish(
        string guildId, string channelId, [FromBody] GuildUnpublishBody body, CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        // No ownership check, and none is possible to need: a caller can only ever unpublish their
        // own tracks, because the only user id this method uses is the authenticated one.
        await voice.RecordTracksClosedAsync(Room(channelId), UserId, body.TrackNames, ct);
        return NoContent();
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

    /// <summary>
    /// What this member may send, from their permissions and the room's ceilings together.
    /// </summary>
    private async Task<VoiceMediaRights> ResolveRightsAsync(
        string guildId, string channelId, CancellationToken ct)
    {
        var maySpeak = await permissions.CanUserPerformActionAsync(
            UserId, channelId, Permissions.Speak);
        var mayStream = await permissions.CanUserPerformActionAsync(
            UserId, channelId, Permissions.Stream);

        if (!mayStream) return maySpeak ? VoiceMediaRights.AudioOnly : VoiceMediaRights.Listener;

        var decision = await voice.EvaluateVideoPublishAsync(
            Room(channelId), UserId, VoiceVideoRequest.Best, ct);

        return decision.VideoAllowed
            ? maySpeak ? VoiceMediaRights.Full : new VoiceMediaRights(false, true, true)
            : maySpeak ? VoiceMediaRights.AudioOnly : VoiceMediaRights.Listener;
    }

    private async Task TryRevokeVideoAsync(string channelId, string identity, CancellationToken ct)
    {
        try
        {
            await sfu.UpdateRightsAsync(
                Room(channelId), identity, VoiceMediaRights.AudioOnly, ct);
        }
        catch (VoiceMediaException ex)
        {
            logger.LogWarning(ex,
                "Could not narrow {Identity}'s publish rights in channel {ChannelId} after refusing "
                + "their video", identity, channelId);
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

}
