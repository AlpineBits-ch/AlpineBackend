using System.Security.Claims;
using Echo.Realtime.Devices;
using Guild.Application.Dtos;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Controllers;

/// <summary>The ephemeral "come and join me in here" ring.</summary>
[Authorize]
[ApiController]
[Route("api/v1/guilds")]
public class GuildVoiceRingController(
    VoiceRingService rings,
    VoiceRingStore store,
    MicroserviceContext db,
    DeviceIdResolver devices) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private DateTime Now => rings.Clock.GetUtcNow().UtcDateTime;

    /// <summary>The caller's device, or null when it cannot be placed.</summary>
    private async Task<string?> DeviceAsync(CancellationToken ct)
    {
        var device = await devices.ResolveAsync(Request, UserId, ct);
        return device.IsUnknown ? null : device.DeviceId;
    }

    /// <summary>Asks one member into a voice channel: quietly, loudly, or both.</summary>
    [HttpPost("{guildId}/channels/{channelId}/voice/rings")]
    public async Task<IActionResult> Ring(
        string guildId, string channelId, RingVoiceChannelDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.TargetUserId)) return BadRequest("A target user is required.");
        if (!Enum.IsDefined(dto.Delivery)) return BadRequest("Unknown delivery.");

        var deviceId = await DeviceAsync(ct);
        var result = await rings.RingAsync(
            UserId, deviceId, guildId, channelId, dto.TargetUserId, dto.Delivery, ct);

        return result.Outcome switch
        {
            VoiceRingOutcome.Created => Ok(VoiceRingDto.From(result.Ring!, Now)),

            // No ring to describe, so no VoiceRingDto.
            VoiceRingOutcome.MessageSent => Ok(new VoiceInviteSentDto(result.ConversationId!)),

            // The one refusal unique to a message invitation.
            VoiceRingOutcome.MessageRefused =>
                StatusCode(403, new VoiceRingRefusalDto("RecipientPolicy", 0)),

            // The same 200 as a fresh ring, deliberately.
            VoiceRingOutcome.AlreadyPending => Ok(VoiceRingDto.From(result.Ring!, Now)),

            VoiceRingOutcome.SelfRing => BadRequest("You are already in the channel."),
            VoiceRingOutcome.NotAVoiceChannel => BadRequest("Channel is not a voice channel"),
            VoiceRingOutcome.ChannelNotFound => NotFound(),
            VoiceRingOutcome.TargetNotAMember => NotFound(),
            VoiceRingOutcome.InviterNotInChannel =>
                Forbid(),
            VoiceRingOutcome.TargetCannotJoinChannel =>
                StatusCode(403, new VoiceRingRefusalDto("TargetCannotJoinChannel", 0)),

            // A block reads the same as any other reason this person cannot be reached.
            VoiceRingOutcome.Unavailable =>
                StatusCode(403, new VoiceRingRefusalDto("Unavailable", 0)),

            VoiceRingOutcome.TargetAlreadyInChannel =>
                Conflict(new VoiceRingRefusalDto("TargetAlreadyInChannel", 0)),

            VoiceRingOutcome.Throttled => StatusCode(429, new VoiceRingRefusalDto(
                result.Refusal!, (int)Math.Ceiling(result.RetryAfter.TotalSeconds))),

            _ => StatusCode(500),
        };
    }

    /// <summary>Every ring currently asking the caller into a channel.</summary>
    [HttpGet("voice/rings/pending")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var pending = await store.PendingForTargetAsync(UserId, ct);
        if (pending.Count == 0) return Ok(Array.Empty<VoiceRingDto>());

        var channelIds = pending.Select(r => r.ChannelId).Distinct().ToList();
        var names = await db.Channels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var now = Now;
        return Ok(pending
            .Select(r => VoiceRingDto.From(r, now, names.GetValueOrDefault(r.ChannelId)))
            .ToList());
    }

    /// <summary>The target says yes.</summary>
    [HttpPost("voice/rings/{ringId}/accept")]
    public Task<IActionResult> Accept(string ringId, CancellationToken ct) =>
        AnswerAsync(ringId, VoiceRingStatus.Accepted, ct);

    /// <summary>The target says no. Also the gesture that shuts this inviter out of ringing them
    /// again for a while - see <see cref="VoiceRingThrottle"/>.</summary>
    [HttpPost("voice/rings/{ringId}/decline")]
    public Task<IActionResult> Decline(string ringId, CancellationToken ct) =>
        AnswerAsync(ringId, VoiceRingStatus.Declined, ct);

    /// <summary>The inviter takes it back.</summary>
    [HttpDelete("voice/rings/{ringId}")]
    public async Task<IActionResult> Cancel(string ringId, CancellationToken ct)
    {
        var ring = await store.LoadAsync(ringId, ct);
        if (ring is null) return NotFound();

        // Only the person who sent it.
        if (ring.InviterId != UserId) return Forbid();

        var transition = await rings.ResolveAsync(
            ringId, VoiceRingStatus.Cancelled, VoiceRingReason.InviterCancelled, await DeviceAsync(ct), ct);

        if (transition.AlreadyResolved)
            return Conflict(VoiceRingDto.From(transition.Ring!, Now));

        return Ok(VoiceRingDto.From(transition.Ring!, Now));
    }

    private async Task<IActionResult> AnswerAsync(string ringId, VoiceRingStatus status, CancellationToken ct)
    {
        var ring = await store.LoadAsync(ringId, ct);
        if (ring is null) return NotFound();
        if (ring.TargetUserId != UserId) return Forbid();

        var deviceId = await DeviceAsync(ct);

        // Either the second device's late answer, or an answer that arrived after the deadline.
        if (!ring.IsPending(Now))
        {
            var lapsed = await rings.ResolveAsync(
                ringId, VoiceRingStatus.Expired, VoiceRingReason.TimedOut, null, ct);
            var current = lapsed.Ring ?? ring;

            if (!lapsed.Transitioned && deviceId is not null)
                await rings.DismissDeviceAsync(current, deviceId, ct);

            return Conflict(VoiceRingDto.From(current, Now));
        }

        if (status == VoiceRingStatus.Accepted && await ChannelGoneForAsync(ring, ct))
        {
            await rings.ResolveAsync(
                ring.Id, VoiceRingStatus.Cancelled, VoiceRingReason.ChannelGone, deviceId, ct);
            return StatusCode(410, new VoiceRingRefusalDto(VoiceRingReason.ChannelGone, 0));
        }

        var transition = await rings.ResolveAsync(ringId, status, null, deviceId, ct);

        if (transition.NotFound) return NotFound();

        if (transition.AlreadyResolved)
        {
            if (deviceId is not null) await rings.DismissDeviceAsync(transition.Ring!, deviceId, ct);
            return Conflict(VoiceRingDto.From(transition.Ring!, Now));
        }

        var channelName = await db.Channels
            .AsNoTracking()
            .Where(c => c.Id == ring.ChannelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        return Ok(VoiceRingDto.From(transition.Ring!, Now, channelName));
    }

    /// <summary>Whether the channel this ring points at is still somewhere this caller could go.
    /// Deletion, a type change and a permission change are one question here because they have one
    /// answer for the client: the invitation is dead, do not offer a join button.</summary>
    private async Task<bool> ChannelGoneForAsync(VoiceRing ring, CancellationToken ct)
    {
        var channel = await db.Channels
            .AsNoTracking()
            .Select(c => new { c.Id, c.Type })
            .FirstOrDefaultAsync(c => c.Id == ring.ChannelId, ct);

        if (channel is null || channel.Type != Guild.Domain.Enums.ChannelType.Voice) return true;

        return !await rings.CanTargetStillJoinAsync(ring.TargetUserId, ring.ChannelId);
    }
}
