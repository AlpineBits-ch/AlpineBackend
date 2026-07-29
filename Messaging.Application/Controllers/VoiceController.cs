using System.Security.Claims;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Messaging.Application.Dtos.Request;

using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
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
    IHubContext<EchoRealtimeHub> hubContext) : ControllerBase
{
    // Call.GetCacheId(callId) doubles as the lock key -LockedJsonCacheStore namespaces it
    // under "lock:" internally, so it can't collide with the cache entry itself.
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

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

        var deviceTokens = await bus.InvokeAsync<GetDeviceTokenForUserIdResponse>(new GetDeviceTokenForUserIdRequest { UserIds = request.Participants });
        var voipTokens = await bus.InvokeAsync<GetVoipTokenForUserIdResponse>(new GetVoipTokenForUserIdRequest { UserIds = request.Participants });
        await CallPushService.SendIncomingCallAsync(deviceTokens.Tokens, voipTokens.Tokens, new CallPushPayload
        {
            CallId = call.Id,
            ConversationId = call.ConversationId,
            CallerName = response.Profile.UserName,
            CallerAvatarUrl = response.Profile.AvatarUrl,
        });

        return Accepted(call);
    }

    /// <summary>
    /// Authoritative current-state fetch — the catch-up path for clients that missed a
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
        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.Accept(userId), CacheOptions);
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

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.Decline(userId), CacheOptions);
        if (call is null) return NotFound();

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        // The call is only actually over once Decline() has rejected it outright (1:1, or every
        // invitee in a group call has now declined) - only then do the other clients need telling
        // the call ended, mirroring EndCall's notification path below.
        if (call.Status == CallStatus.Rejected)
        {
            var participantIds = call.Participants.Select(p => p.UserId).ToList();
            await Task.WhenAll(participantIds.Select(id => cache.RemoveAsync($"user-call:{id}")));
            await hubContext.Clients.Users(participantIds).SendAsync("call.CallEnded", new { callId = call.Id });

            var cancelRecipientIds = participantIds.Where(id => id != userId).ToList();
            if (cancelRecipientIds.Count > 0)
            {
                var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
                var deviceTokens = await bus.InvokeAsync<GetDeviceTokenForUserIdResponse>(new GetDeviceTokenForUserIdRequest { UserIds = cancelRecipientIds });
                var voipTokens = await bus.InvokeAsync<GetVoipTokenForUserIdResponse>(new GetVoipTokenForUserIdRequest { UserIds = cancelRecipientIds });
                await CallPushService.SendCancelCallAsync(deviceTokens.Tokens, voipTokens.Tokens, new CallPushPayload
                {
                    CallId = call.Id,
                    ConversationId = call.ConversationId,
                    CallerName = callerProfile.Profile?.UserName ?? string.Empty,
                    CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
                });
            }
        }

        return Accepted(call);
    }
    [HttpPut("call/{callId}/end")]
    public async Task<IActionResult> EndCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();

        var call = await callStore.UpdateAsync<Call>(
            Call.GetCacheId(callId), Call.GetCacheId(callId),
            c => c.End(userId), CacheOptions);
        if (call is null) return NotFound();

        var participantIds = call.Participants.Select(p => p.UserId).ToList();

        await Task.WhenAll(participantIds.Select(id => cache.RemoveAsync($"user-call:{id}")));

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await hubContext.Clients.Users(participantIds).SendAsync("call.CallEnded", new { callId });

        var cancelRecipientIds = participantIds.Where(id => id != userId).ToList();
        if (cancelRecipientIds.Count > 0)
        {
            var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
            var deviceTokens = await bus.InvokeAsync<GetDeviceTokenForUserIdResponse>(new GetDeviceTokenForUserIdRequest { UserIds = cancelRecipientIds });
            var voipTokens = await bus.InvokeAsync<GetVoipTokenForUserIdResponse>(new GetVoipTokenForUserIdRequest { UserIds = cancelRecipientIds });
            await CallPushService.SendCancelCallAsync(deviceTokens.Tokens, voipTokens.Tokens, new CallPushPayload
            {
                CallId = call.Id,
                ConversationId = call.ConversationId,
                CallerName = callerProfile.Profile?.UserName ?? string.Empty,
                CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
            });
        }

        return Accepted(call);
    }
}