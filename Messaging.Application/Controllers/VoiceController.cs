using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;

using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/voice")]
public class VoiceController(
    IceServerService iceServerService,
    IMessageBus bus,
    IDistributedCache cache,
    LockedJsonCacheStore callStore,
    DeviceIdResolver devices,
    DirectMessagePolicyService dmPolicy,
    StreamViewerStore viewers,
    MicroserviceContext db,
    VoiceRoomStore rooms,
    VoiceRoomService voice,
    IHubContext<EchoRealtimeHub> hubContext) : ControllerBase
{
    // Call.GetCacheId(callId) doubles as the lock key -LockedJsonCacheStore namespaces it
    // under "lock:" internally, so it can't collide with the cache entry itself.
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    /// <summary>
    /// Reverse index answering "is a call ringing for this user right now", written when the call
    /// is placed and read by <see cref="GetPendingCall"/>.
    /// </summary>
    private static readonly DistributedCacheEntryOptions PendingCallOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
    };

    private static string PendingCallKey(string userId) => $"user-ringing:{userId}";

    /// <summary>
    /// Resolves X-Device-Id and checks it really is one of this user's registered devices.
    /// </summary>
    private async Task<DeviceIdResult> ResolveDeviceAsync(string userId, CancellationToken ct = default) =>
        await devices.ResolveAsync(Request, userId, ct);

    private static IActionResult UnknownDevice(DeviceIdResult device) =>
        new BadRequestObjectResult($"Unknown {DeviceIdentity.HeaderName} '{device.DeviceId}' - register the device first.");

    /// <summary>
    /// The authoritative media state of this call, sufficient on its own for a client to be fully
    /// correct however much it missed.
    /// </summary>
    [HttpGet("call/{callId}/snapshot")]
    public async Task<IActionResult> GetCallSnapshot(string callId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var call = await callStore.LoadAsync<Domain.Entities.Call>(Domain.Entities.Call.GetCacheId(callId));
        if (call is null || !call.IsParticipant(userId)) return NotFound();

        var key = VoiceRoomKey.Call(callId);
        var room = await rooms.LoadAsync(key, ct);
        return Ok(room is null ? VoiceRoomSnapshot.Empty(key) : VoiceRoomSnapshot.From(room));
    }

    [HttpGet("ice-servers")]
    public async Task<IActionResult> GetIceServers()
    {
        var iceServers = await iceServerService.GetIceServersForUser(User.Identity.Name);
        return Ok(iceServers);
    }

    [HttpPost("call")]
    public async Task<IActionResult> CallAsync(CreateCallRequestDto request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var response = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = userId
        });
        if(response.Profile is null) return BadRequest();
        
        // T0-2, call-token path.
        var refusal = await dmPolicy.EvaluateAsync(userId, request.Participants.ToList());
        if (refusal is not null) return DmRefusalResults.ToActionResult(refusal);


        var call = new Call()
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Id = Call.GenerateId(),
            CreatorId = userId,
            Status = CallStatus.Pending,
            ConversationId = request.ConversationId ?? string.Empty,
            Tracks = [],
            Participants = request.Participants
                .Select(p => new CallParticipant() { UserId = p })
                .Append(new CallParticipant() { UserId = userId })
                .ToList()
        };

        call.MarkCreated();

        await cache.SetStringAsync(Call.GetCacheId(call.Id), JsonSerializer.Serialize(call), new DistributedCacheEntryOptions()
        {
            SlidingExpiration = TimeSpan.FromMinutes(40)
        });

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await hubContext.Clients.Users(request.Participants).SendAsync("call.IncomingCall", call);

        // Everyone else in the conversation is told a call is happening, not that they are being
        // rung.
        await AnnounceConversationCallAsync(call, "Ongoing");

        // Neither of the two ways a callee hears about this is guaranteed.
        await Task.WhenAll(request.Participants.Select(p =>
            cache.SetStringAsync(PendingCallKey(p), call.Id, PendingCallOptions)));

        var pushTokens = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = request.Participants });
        await CallPushService.SendIncomingCallAsync(pushTokens.Of(PushTokenKind.Fcm), pushTokens.Of(PushTokenKind.ApnsVoip), new CallPushPayload
        {
            CallId = call.Id,
            ConversationId = call.ConversationId,
            CallerId = userId,
            // The one string the native call screen has to show.
            CallerName = string.IsNullOrWhiteSpace(response.Profile.UserName)
                ? "Incoming call"
                : response.Profile.UserName,
            CallerAvatarUrl = response.Profile.AvatarUrl,
        });

        return Accepted(call);
    }

    /// <summary>
    /// Authoritative current-state fetch - the catch-up path for clients that missed a
    /// `call.ParticipantJoined`/`call.CallEnded` SignalR event (e.g. a reconnect gap mid-call).
    /// </summary>
    [HttpGet("call/{callId}")]
    public async Task<IActionResult> GetCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var call = await callStore.LoadAsync<Call>(Call.GetCacheId(callId));
        if (call == null || !call.IsParticipant(userId)) return NotFound();

        return Ok(call);
    }

    /// <summary>
    /// The call ringing for the caller right now, or <c>204</c> when there is none.
    /// </summary>
    [HttpGet("call/pending")]
    public async Task<IActionResult> GetPendingCall()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var callId = await cache.GetStringAsync(PendingCallKey(userId));
        if (string.IsNullOrWhiteSpace(callId)) return NoContent();

        // The index is a hint; the call is the authority.
        var call = await callStore.LoadAsync<Call>(Call.GetCacheId(callId));
        if (call is null || call.Status is not (CallStatus.Pending or CallStatus.Connected)) return NoContent();

        // Still Pending for *this* user specifically: a group call others have already joined is
        // Connected overall while it goes on ringing here, and a call this user already answered or
        // declined on another device must not ring again.
        var me = call.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null || me.Status != CallStatus.Pending) return NoContent();

        return Ok(call);
    }

    /// <summary>
    /// The call going on in <paramref name="conversationId"/> right now, or <c>204</c>.
    /// </summary>
    [HttpGet("conversations/{conversationId}/call")]
    public async Task<IActionResult> GetConversationCall(string conversationId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var isMember = await db.Members
            .AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct);
        if (!isMember) return NotFound();

        var callId = await cache.GetStringAsync(CallService.ConversationCallKey(conversationId), ct);
        if (string.IsNullOrWhiteSpace(callId)) return NoContent();

        // The index is a hint and the call is the authority - same contract as GetPendingCall, and
        // for the same reason: a call ends in half a dozen ways and an index that missed one must
        // degrade to "no call" rather than advertise a dead one.
        var call = await callStore.LoadAsync<Domain.Entities.Call>(Domain.Entities.Call.GetCacheId(callId));
        if (call is null || call.Status is not (CallStatus.Pending or CallStatus.Connected)) return NoContent();

        return Ok(new OngoingCallDto(
            call.Id,
            conversationId,
            call.Status.ToString(),
            call.CreatorId,
            call.CreatedAt,
            call.Participants.Where(p => p.Status == CallStatus.Connected).Select(p => p.UserId).ToList()));
    }

    /// <summary>Tells every member of the call's conversation that its state changed.</summary>
    private async Task AnnounceConversationCallAsync(Domain.Entities.Call call, string status)
    {
        if (string.IsNullOrWhiteSpace(call.ConversationId)) return;

        await cache.SetStringAsync(
            CallService.ConversationCallKey(call.ConversationId), call.Id, CacheOptions);

        var memberIds = await db.Members
            .AsNoTracking()
            .Where(m => m.ConversationId == call.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync();
        if (memberIds.Count == 0) return;

        await hubContext.Clients.Users(memberIds).SendAsync("conversation.CallStateChanged", new
        {
            conversationId = call.ConversationId,
            callId = call.Id,
            status,
            reason = (string?)null,
            participantIds = call.Participants
                .Where(p => p.Status == CallStatus.Connected)
                .Select(p => p.UserId)
                .ToList(),
        });
    }

    // ── Screen share viewers ──────────────────────────────────────────────────

    /// <summary>Announces the caller as watching <paramref name="shareId"/> in this call, or
    /// refreshes that claim. Expires on its own - see <see cref="StreamViewerStore"/>.</summary>
    [HttpPost("call/{callId}/shares/{shareId}/watch")]
    public Task<IActionResult> WatchCallShare(string callId, string shareId, CancellationToken ct) =>
        UpdateCallWatchAsync(callId, shareId, watching: true, ct);

    /// <summary>Stops counting the caller as a viewer of <paramref name="shareId"/>.</summary>
    [HttpDelete("call/{callId}/shares/{shareId}/watch")]
    public Task<IActionResult> UnwatchCallShare(string callId, string shareId, CancellationToken ct) =>
        UpdateCallWatchAsync(callId, shareId, watching: false, ct);

    /// <summary>Everyone currently watching each live share in this call, as
    /// <c>shareId -&gt; userIds</c> - the catch-up read for someone who joined mid-stream.</summary>
    [HttpGet("call/{callId}/shares/viewers")]
    public async Task<IActionResult> GetCallShareViewers(string callId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var call = await callStore.LoadAsync<Domain.Entities.Call>(Domain.Entities.Call.GetCacheId(callId));
        if (call is null || !call.IsParticipant(userId)) return NotFound();

        var snapshot = await viewers.SnapshotAsync(VoiceRoomKey.Call(callId).ViewerScope, ct);
        return Ok(snapshot.ToDictionary(s => s.Key, s => s.Value));
    }

    private async Task<IActionResult> UpdateCallWatchAsync(string callId, string shareId, bool watching, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var call = await callStore.LoadAsync<Domain.Entities.Call>(Domain.Entities.Call.GetCacheId(callId));
        if (call is null) return NotFound();

        // Connected, not merely invited: watching is a claim about media that only flows to someone
        // actually in the call, and an invitee who never answered has no business appearing in a
        // viewer count.
        var me = call.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null || me.Status != CallStatus.Connected) return Forbid();

        var scope = VoiceRoomKey.Call(callId).ViewerScope;
        var snapshot = watching
            ? await viewers.WatchAsync(scope, shareId, userId, ct)
            : await viewers.UnwatchAsync(scope, shareId, userId, ct);

        var viewerIds = snapshot.TryGetValue(shareId, out var ids) ? ids : [];
        await voice.AnnounceShareViewersAsync(VoiceRoomKey.Call(callId), shareId, viewerIds, ct);

        return Ok(new { callId, shareId, viewerCount = viewerIds.Count, viewerIds });
    }

    [HttpPut("call/{callId}/accept")]
    public async Task<IActionResult> AcceptCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();

        // Locked: races against CloudflareController.CreateSession/ExchangeParticipantJoined
        // for the same callId when the caller publishes its audio track around the same time
        // the callee accepts -without the lock, whichever save lands last silently wins and
        // can wipe out the other side's CfSessionId/AudioTrackName (the original bug: the
        // Tauri client's syncParticipants() backfill then finds those fields null and never
        // subscribes to the caller's audio).
        var device = await ResolveDeviceAsync(userId);
        if (device.IsUnknown) return UnknownDevice(device);

        // Media state of any device this accept supersedes lives in the voice room, so it is read
        // here and handed to the domain for the takeover event to carry.
        var superseded = (await rooms.LoadAsync(VoiceRoomKey.Call(callId)))?.Find(userId);

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.Accept(userId, device.DeviceId, superseded?.CfSessionId, superseded?.AudioTrackName),
            CacheOptions);
        if (call is null) return NotFound();

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        // Re-announced so the conversation-level "call in progress" indicator names the people
        // actually on the call, not just those on it when it was placed.
        await AnnounceConversationCallAsync(call, "Ongoing");

        return Accepted(call);
    }

    [HttpPut("call/{callId}/decline")]
    public async Task<IActionResult> DeclineCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var device = await ResolveDeviceAsync(userId);
        if (device.IsUnknown) return UnknownDevice(device);

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.Decline(userId, device.DeviceId), CacheOptions);
        if (call is null) return NotFound();

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        // The call is only actually over once Decline() has rejected it outright (1:1, or every
        // invitee in a group call has now declined) - only then do the other clients need telling
        // the call ended.
        if (call.Status == CallStatus.Rejected)
        {
            await CallEndNotifier.NotifyAsync(call, CallEndReason.Declined, userId, bus, cache, hubContext);
        }

        return Accepted(call);
    }

    /// <summary>Removes just the caller from a still-active call, leaving it running for any
    /// other connected participants (see Call.Leave). Distinct from End, which force-terminates
    /// the call for everyone.</summary>
    [HttpPut("call/{callId}/leave")]
    public async Task<IActionResult> LeaveCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var device = await ResolveDeviceAsync(userId);
        if (device.IsUnknown) return UnknownDevice(device);

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.Leave(userId, device.DeviceId), CacheOptions);
        if (call is null) return NotFound();

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        // Leave() only completes the call outright when it dropped to zero connected participants -
        // otherwise it either keeps running normally or moved to the one-participant-alone state,
        // both handled via the published events above (CallParticipantLeft / CallWentAlone).
        if (call.Status == CallStatus.Completed)
        {
            await CallEndNotifier.NotifyAsync(call, CallEndReason.AllParticipantsLeft, userId, bus, cache, hubContext);
        }

        return Accepted(call);
    }

    [HttpPut("call/{callId}/end")]
    public async Task<IActionResult> EndCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();

        // Call.End() is unconditional, and unlike Accept/Decline/Leave - which each resolve the
        // caller's own participant row and no-op for a stranger - nothing here tied the caller to
        // the call.
        var existing = await callStore.LoadAsync<Call>(Call.GetCacheId(callId));
        if (existing is null || !existing.IsParticipant(userId)) return NotFound();

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.End(CallEndReason.UserEnded), CacheOptions);
        if (call is null) return NotFound();

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await CallEndNotifier.NotifyAsync(call, CallEndReason.UserEnded, userId, bus, cache, hubContext);

        return Accepted(call);
    }
}