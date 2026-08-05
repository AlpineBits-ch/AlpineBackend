using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Messaging.Application.Dtos.Request;

using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
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

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.Accept(userId, device.DeviceId), CacheOptions);
        if (call is null) return NotFound();

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

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