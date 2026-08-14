using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Echo.Entitlements.Wire;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Transport;
using Echo.Voice.Rooms;

using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/channels/{channelId}/voice")]
public class GuildVoiceController(
    GuildPermissionService permissions,
    IHubContext<EchoRealtimeHub> hub,
    IDistributedCache cache,
    IVoiceMediaTransport media,
    MicroserviceContext db,
    DeviceIdResolver devices,
    GuildVoiceActivityStore activity,
    StreamViewerStore viewers,
    VoiceRoomService voice,
    VoiceRoomStore rooms,
    IMessageBus bus) : ControllerBase
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static VoiceRoomKey Room(string channelId) => VoiceRoomKey.Channel(channelId);

    private Task<DeviceIdResult> ResolveDeviceAsync(CancellationToken ct = default) =>
        devices.ResolveAsync(Request, UserId, ct);

    [HttpPost("join")]
    public async Task<IActionResult> Join(string guildId, string channelId, CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.Connect))
            return Forbid();

        var channel = await db.Channels
            .AsNoTracking()
            .Select(c => new { c.Id, c.GuildId, c.Type })
            .FirstOrDefaultAsync(c => c.Id == channelId && c.GuildId == guildId, ct);

        if (channel is null) return NotFound();
        if (channel.Type != Guild.Domain.Enums.ChannelType.Voice) return BadRequest("Channel is not a voice channel");

        var device = await ResolveDeviceAsync(ct);
        if (device.IsUnknown)
            return BadRequest($"Unknown {DeviceIdentity.HeaderName} '{device.DeviceId}' - register the device first.");
        var deviceId = device.DeviceId;

        // A user can only be in one voice channel, on one device, at a time, app-wide.
        var existingChannelJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(UserId), ct);
        if (existingChannelJson is not null
            && JsonSerializer.Deserialize<UserVoiceLocation>(existingChannelJson) is { } existing)
        {
            if (existing.ChannelId == channelId)
            {
                if (existing.DeviceId is not null && existing.DeviceId != deviceId)
                    await TakeoverDeviceAsync(guildId, channelId, UserId, existing.DeviceId, deviceId, ct);
            }
            else
            {
                await LeaveChannelAsync(existing.ChannelId, UserId, ct);
            }
        }

        // Roster first, media later.
        var admission = await voice.AdmitAsync(Room(channelId), UserId, deviceId, guildId, ct);
        var room = admission.Room;

        // Guild-level index, so the server list can answer "is anyone in voice here" without
        // reading every channel of every guild. Derived from the roster, never the source of truth.
        await activity.AddParticipantAsync(guildId, channelId, UserId, ct);

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(UserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = channelId, GuildId = guildId, DeviceId = deviceId }),
            CacheOptions, ct);
        await cache.SetStringAsync(
            VoiceReconciler.LivenessKey(UserId), Room(channelId).ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VoiceReconciler.LivenessTtl },
            ct);

        // Guild-wide presence fan-out.
        var onlineUserIds = await GetOnlineGuildMemberIdsAsync(guildId);
        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserJoinedVoice",
            new { userId = UserId, channelId, guildId }, ct);

        await bus.PublishAsync(new VoiceStateForBots { GuildId = guildId, UserId = UserId, ChannelId = channelId });

        var snapshot = VoiceRoomSnapshot.From(room);
        var degradations = await DescribeAsync(admission, guildId);

        // Byte-identical to what a v1 client already receives whenever nothing was reduced, which
        // is every join in every guild that is inside its plan.
        return degradations.Count == 0
            ? Ok(snapshot)
            : Ok(EntitlementResponses.WithDegradations(snapshot, degradations));
    }

    /// <summary>The join's degradation as the client reads it.</summary>
    private async Task<IReadOnlyList<EntitlementDegradationDto>> DescribeAsync(
        VoiceAdmission admission, string guildId)
    {
        if (admission.OverCapacity is null) return [];

        var sellsUpgrades = Env.License.IsHosted && Env.License.IsBillingConfigured;

        var needsGuildRemedy = sellsUpgrades
                               && admission.OverCapacity.Cause.BoundBy == EntitlementBoundBy.Guild;

        var canManageGuild = needsGuildRemedy
                             && await permissions.CanUserPerformActionOnGuildAsync(
                                 UserId, guildId, Permissions.ManageGuild);

        return admission.Describe(sellsUpgrades, canManageGuild);
    }

    /// <summary>Same user, same channel, a different device just joined - transfer the connection
    /// instead of running two. Tells exactly the old device to disconnect, and best-effort closes
    /// its stale Cloudflare session server-side too.</summary>
    private async Task TakeoverDeviceAsync(string guildId, string channelId, string userId, string oldDeviceId, string newDeviceId, CancellationToken ct)
    {
        string? oldMediaSessionId = null;
        string? oldAudioTrackName = null;

        await rooms.MutateExistingAsync(Room(channelId), r =>
        {
            var participant = r.Find(userId);
            if (participant is null) return;
            oldMediaSessionId = participant.MediaSessionId;
            oldAudioTrackName = participant.AudioTrackName;
            participant.DeviceId = newDeviceId;
            participant.MediaSessionId = null;
            participant.AudioTrackName = null;
        }, ct);

        await hub.Clients.Group(EchoRealtimeHub.DeviceGroup(userId, oldDeviceId))
            .SendAsync("guild.voice.KickedByOtherDevice", new { channelId, guildId }, ct);

        if (oldMediaSessionId is null || oldAudioTrackName is null) return;
        try
        {
            await media.CloseTracksAsync(oldMediaSessionId, [oldAudioTrackName], ct);
        }
        catch (VoiceMediaException)
        {
            // Best-effort - the old device still tears itself down client-side from the kick above.
        }
    }

    [HttpPost("leave")]
    public async Task<IActionResult> Leave(string guildId, string channelId, CancellationToken ct)
    {
        await LeaveChannelAsync(channelId, UserId, ct);
        return NoContent();
    }

    /// <summary>
    /// The authoritative state of this channel's voice room, sufficient on its own for a client to
    /// be fully correct however much it missed.
    /// </summary>
    [HttpGet]
    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(string guildId, string channelId, CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.ViewChannel))
            return Forbid();

        var room = await rooms.LoadAsync(Room(channelId), ct);
        return Ok(room is null
            ? VoiceRoomSnapshot.Empty(Room(channelId), guildId)
            : VoiceRoomSnapshot.From(room));
    }

    /// <summary>
    /// Announces that the caller is watching <paramref name="shareId"/>, or refreshes that claim.
    /// </summary>
    [HttpPost("shares/{shareId}/watch")]
    public Task<IActionResult> WatchShare(string guildId, string channelId, string shareId, CancellationToken ct) =>
        UpdateWatchAsync(channelId, shareId, watching: true, ct);

    /// <summary>Stops counting the caller as a viewer of <paramref name="shareId"/>.</summary>
    [HttpDelete("shares/{shareId}/watch")]
    public Task<IActionResult> UnwatchShare(string guildId, string channelId, string shareId, CancellationToken ct) =>
        UpdateWatchAsync(channelId, shareId, watching: false, ct);

    private async Task<IActionResult> UpdateWatchAsync(string channelId, string shareId, bool watching, CancellationToken ct)
    {
        var room = await rooms.LoadAsync(Room(channelId), ct);
        if (room is null) return NotFound();

        // Membership of the channel, not merely permission to view it: watching is a claim about
        // media that is only pulled from inside the channel.
        if (room.Find(UserId) is null) return Forbid();

        // A share nobody is publishing has no viewers to report, and letting one be named here
        // would let any participant mint counts for shares that do not exist.
        if (!room.Participants.Any(p => p.ActiveScreenShares.Any(s => s.ShareId == shareId)))
            return NotFound();

        var scope = Room(channelId).ViewerScope;
        var snapshot = watching
            ? await viewers.WatchAsync(scope, shareId, UserId, ct)
            : await viewers.UnwatchAsync(scope, shareId, UserId, ct);

        var viewerIds = snapshot.TryGetValue(shareId, out var ids) ? ids : [];
        await voice.AnnounceShareViewersAsync(Room(channelId), shareId, viewerIds, ct);

        return Ok(new { channelId, shareId, viewerCount = viewerIds.Count, viewerIds });
    }

    /// <summary>Everyone currently watching each live share in this channel, as
    /// <c>shareId -&gt; userIds</c>.</summary>
    [HttpGet("shares/viewers")]
    public async Task<IActionResult> GetShareViewers(string guildId, string channelId, CancellationToken ct)
    {
        if (!await permissions.CanUserPerformActionAsync(UserId, channelId, Permissions.ViewChannel))
            return Forbid();

        var snapshot = await viewers.SnapshotAsync(Room(channelId).ViewerScope, ct);
        return Ok(snapshot.ToDictionary(s => s.Key, s => s.Value));
    }

    internal async Task LeaveChannelAsync(string channelId, string userId, CancellationToken ct)
    {
        var before = await rooms.LoadAsync(Room(channelId), ct);
        var ownedShareIds = before?.Find(userId)?.ActiveScreenShares.Select(s => s.ShareId).ToList() ?? [];
        if (before?.Find(userId) is null) return;

        var room = await voice.LeaveAsync(Room(channelId), userId, ct);

        await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId), ct);
        await cache.RemoveAsync(VoiceReconciler.LivenessKey(userId), ct);

        var guildId = room?.GuildId ?? before.GuildId ?? string.Empty;
        await activity.RemoveParticipantAsync(guildId, channelId, userId, ct);

        // Someone who is not in the channel is watching nothing in it, and a share whose owner
        // left has no audience to report.
        var scope = Room(channelId).ViewerScope;
        await viewers.RemoveViewerAsync(scope, userId, ct);
        if (ownedShareIds.Count > 0) await viewers.RemoveSharesAsync(scope, ownedShareIds, ct);

        var onlineUserIds = await GetOnlineGuildMemberIdsAsync(guildId);
        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
            new { userId, channelId, guildId }, ct);

        await bus.PublishAsync(new VoiceStateForBots { GuildId = guildId, UserId = userId, ChannelId = null });
    }

    private async Task<List<string>> GetOnlineGuildMemberIdsAsync(string guildId)
    {
        return await db.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => m.UserId)
            .ToListAsync();
    }
}
