using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Messaging.Application.Dtos.Request;

using Messaging.Application.Services;
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
    IHubContext<EchoRealtimeHub> hubContext) : ControllerBase
{
    // Call.GetCacheId(callId) doubles as the lock key -LockedJsonCacheStore namespaces it
    // under "lock:" internally, so it can't collide with the cache entry itself.
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    /// <summary>Resolves X-Device-Id and checks it really is one of this user's registered
    /// devices. Absent header still falls back to the shared "default" bucket (pre-update builds
    /// keep working, with the old single-device behaviour); a header naming an unknown device is
    /// rejected, because keying call state on an id nothing else will match is the very failure the
    /// device id exists to prevent.</summary>
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
        
        // verify that the user is allowed to make this call


        foreach (var participant in request.Participants)
        {
            if (!response.Profile.Relationships.Any(r => r.UserId == participant && r.Status == RelationshipStatus.Accepted))
            {
                return BadRequest("You are not allowed to make this call");
            }

        }


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

        var pushTokens = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = request.Participants });
        await CallPushService.SendIncomingCallAsync(pushTokens.Of(PushTokenKind.Fcm), pushTokens.Of(PushTokenKind.ApnsVoip), new CallPushPayload
        {
            CallId = call.Id,
            ConversationId = call.ConversationId,
            CallerName = response.Profile.UserName,
            CallerAvatarUrl = response.Profile.AvatarUrl,
        });

        return Accepted(call);
    }

    /// <summary>
    /// Authoritative current-state fetch — the catch-up path for clients that
    /// missed a `call.ParticipantJoined`/`call.CallEnded` SignalR event (e.g. a
    /// reconnect gap mid-call). Mirrors GuildVoiceController.GetVoiceState.
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
        // the call ended. A decline that's actually a stale cross-device race (see Call.Decline)
        // leaves Status alone, so this doesn't fire in that case.
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

        // Leave() only completes the call outright when it dropped to zero connected
        // participants - otherwise it either keeps running normally or moved to the
        // one-participant-alone state, both handled via the published events above
        // (CallParticipantLeft / CallWentAlone).
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
        // the call. Any authenticated user holding a call id (an invitee who declined still has
        // one) could terminate the call for everyone.
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