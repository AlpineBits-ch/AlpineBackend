using System.Security.Claims;
using System.Text.Json;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Hubs;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/voice")]
public class VoiceController(IceServerService iceServerService, IMessageBus bus, IDistributedCache cache, IHubContext<VoiceHub> hubContext) : ControllerBase
{
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

        await cache.SetStringAsync(Call.GetCacheId(call.Id), JsonSerializer.Serialize(call), new DistributedCacheEntryOptions()
        {
            SlidingExpiration = TimeSpan.FromMinutes(40)
        });
        await hubContext.Clients.Users(request.Participants).SendAsync("IncomingCall", call);
        return Accepted(call);
    }

    [HttpPut("call/{callId}/accept")]
    public async Task<IActionResult> AcceptCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();
        
        var serializedCall = await cache.GetStringAsync(Call.GetCacheId(callId));
        if (string.IsNullOrWhiteSpace(serializedCall))
        {
            return NotFound();
        }
        
        var call = JsonSerializer.Deserialize<Call>(serializedCall);
        if (call == null)
        {
            return NotFound();
        }
        
        call.Accept(userId);

        await cache.SetStringAsync(Call.GetCacheId(callId), JsonSerializer.Serialize(call),
            new DistributedCacheEntryOptions()
            {
                SlidingExpiration = TimeSpan.FromMinutes(40)
            });

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
        var serializedCall = await cache.GetStringAsync(Call.GetCacheId(callId));
        if (string.IsNullOrWhiteSpace(serializedCall))
        {
            return NotFound();
        }
        
        var call = JsonSerializer.Deserialize<Call>(serializedCall);
        if (call == null)
        {
            return NotFound();
        }
        call.Decline(userId);

        await cache.SetStringAsync(Call.GetCacheId(call.Id), JsonSerializer.Serialize(call), new DistributedCacheEntryOptions()
        {
            SlidingExpiration = TimeSpan.FromMinutes(40)
        });

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        return Accepted(call);
    }
    [HttpPut("call/{callId}/end")]
    public async Task<IActionResult> EndCall(string callId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return BadRequest();
        var serializedCall = await cache.GetStringAsync(Call.GetCacheId(callId));
        if (string.IsNullOrWhiteSpace(serializedCall))
        {
            return NotFound();
        }
        
        var call = JsonSerializer.Deserialize<Call>(serializedCall);
        if (call == null)
        {
            return NotFound();
        }
        
        call.End(userId);

        var participantIds = call.Participants.Select(p => p.UserId).ToList();

        await cache.SetStringAsync(Call.GetCacheId(callId), JsonSerializer.Serialize(call),
            new DistributedCacheEntryOptions()
            {
                SlidingExpiration = TimeSpan.FromMinutes(40)
            });

        await Task.WhenAll(participantIds.Select(id => cache.RemoveAsync($"user-call:{id}")));

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await hubContext.Clients.Users(participantIds).SendAsync("CallEnded", new { callId });
        return Accepted(call);
    }
}