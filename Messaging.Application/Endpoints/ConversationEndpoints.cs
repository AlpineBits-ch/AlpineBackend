using System.Security.Claims;
using System.Text.Json;
using Domain;
using Facet.Extensions;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

[Authorize]
public class ConversationEndpoints
{
    
    
    /// <summary>How long a consume-tokens answer is replayed rather than re-consumed.</summary>
    public static readonly TimeSpan ConsumeTokenReplayWindow = TimeSpan.FromMinutes(2);

    /// <summary>Distinct consume calls one caller may make inside the replay window.</summary>
    public const int MaxConsumeCallsPerWindow = 20;

    [WolverinePost("/api/v1/conversations/consume-tokens")]
    public async Task<IResult> FetchTokensForUsers(ConsumeMlsDeviceTokensForUserRequest request,
        [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache)
    {
        var ownUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(ownUserId is null) return Results.Unauthorized();


        if (!await IsBefriendedWithUsers(ownUserId, request.UserIds.ToList(), messageBus))
        {
            return Results.BadRequest("User is not friends with the users you are trying to consume tokens for");
        }

        var targets = request.UserIds.Distinct().OrderBy(u => u, StringComparer.Ordinal).ToList();
        var replayKey = $"mls-consume:{ownUserId}:{string.Join(",", targets)}";

        // Idempotent inside the window.
        var cached = await cache.GetStringAsync(replayKey);
        if (cached is not null)
        {
            var replayed = JsonSerializer.Deserialize<ConsumeMlsDeviceTokensForUserResponse>(cached);
            if (replayed is not null) return Results.Ok(replayed);
        }

        var budgetKey = $"mls-consume-budget:{ownUserId}";
        var spent = int.TryParse(await cache.GetStringAsync(budgetKey), out var parsed) ? parsed : 0;
        if (spent >= MaxConsumeCallsPerWindow)
        {
            return Results.Json(
                new { retryAfterSeconds = (int)ConsumeTokenReplayWindow.TotalSeconds },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var tokens = await messageBus.InvokeAsync<ConsumeMlsDeviceTokensForUserResponse>(request);

        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ConsumeTokenReplayWindow };
        await cache.SetStringAsync(replayKey, JsonSerializer.Serialize(tokens), options);
        await cache.SetStringAsync(budgetKey, (spent + 1).ToString(), options);

        return Results.Ok(tokens);
    }
    
    [WolverinePost( "/api/v1/conversations")]
    public async Task<IResult> CreateConversation(CreateConversationDto createDto,
        [FromQuery] bool allowPartialDeviceCoverage,
        [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx )
    {
        
        
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return Results.Unauthorized();
        
        var response = await messageBus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = userId
        });
        
        if(response.Profile is null) return Results.BadRequest("Profile not found");
        
        var memberProfiles = new List<ProfileDto>();

        foreach (var member in createDto.Members)
        {
            var memberResponse = await messageBus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
            {
                UserId = member.UserId
            });
            
            if(memberResponse.Profile is null) return Results.BadRequest("Profile not found");
            
            memberProfiles.Add(memberResponse.Profile);
        }
        
        var befriendedUserIds = response.Profile.Relationships
            .Where(r => r.Status == RelationshipStatus.Accepted)
            .Select(r => r.UserId).Where(u => u != userId).ToList();
        
        // check if all user ids are friends 
        
        foreach (var createConversationMemberDto in createDto.Members)
        {
            // TODO: We have to check the users privacy bit settings here, but for now default to this
            if (!befriendedUserIds.Contains(createConversationMemberDto.UserId))
                return Results.BadRequest("User cannot be added to conversation if not friends");
        }


        var selfMember = new CreateConversationMemberParams()
        {
            UserId = userId,
            PublicKey = Array.Empty<byte>(),
            CachedUserName = response.Profile.UserName,
            CachedUserHash = response.Profile.Hash,
        };


        // No token consumption here.
        var unreachableDevices = new List<UnreachableDeviceDto>();

        if (createDto.Encryption == ChannelEncryptionState.Encrypted)
        {
            // Per device, not per user.
            unreachableDevices = await ResolveUnreachableDevicesAsync(createDto, messageBus);

            // Permissive by default during the rollout.
            if (unreachableDevices.Count > 0
                && MlsPolicy.RejectUnreachableDevicesOnCreate
                && !allowPartialDeviceCoverage)
            {
                return Results.BadRequest(new CreateConversationRejectedDto
                {
                    Reason = "Some member devices had no MLS key package available, so they can never "
                             + "read this conversation. Retry once they are online, or pass "
                             + "?allowPartialDeviceCoverage=true to create it anyway.",
                    UnreachableDevices = unreachableDevices,
                });
            }
        }


        var conversation = Conversation.Create(new CreateConversationParams()
        {
            Encryption = createDto.Encryption,
            Members = createDto.Members.Select(m => new CreateConversationMemberParams()
            {
                UserId = m.UserId,
                PublicKey = Array.Empty<byte>(),
                CachedUserName = memberProfiles.Single(p => p.UserId == m.UserId).UserName,
                CachedUserHash = memberProfiles.Single(p => p.UserId == m.UserId).Hash,
            }).Concat([selfMember]).ToList(),
            Name = createDto.Name,
            MlsEpoch = createDto.MlsEpoch,
            MlsGroupId = createDto.MlsGroupId,
            MlsGroupInfo = createDto.MlsGroupInfo,
        });
        ctx.Conversations.Add(conversation);

        // A conversation created encrypted starts on generation 1.
        if (createDto.Encryption == ChannelEncryptionState.Encrypted)
        {
            ctx.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
            {
                ContextId = conversation.Id,
                ConversationId = conversation.Id,
                Generation = 1,
                MlsGroupId = createDto.MlsGroupId!,
                MlsGroupInfo = createDto.MlsGroupInfo,
                Epoch = createDto.MlsEpoch ?? 0,
                ActivatedByUserId = userId,
                ActivatedAt = DateTimeOffset.UtcNow,
            }));
        }

        foreach (var deviceWelcome in createDto.DeviceWelcomes)
        {
            ctx.PendingWelcomes.Add(PendingWelcome.Create(new CreatePendingWelcomeParams
            {
                ContextId = conversation.Id,
                ConversationId = conversation.Id,
                UserId = deviceWelcome.UserId,
                DeviceId = deviceWelcome.DeviceId,
                Welcome = deviceWelcome.Welcome,
                Generation = 1,
                Epoch = createDto.MlsEpoch ?? 0,
            }));
        }


        var dto = conversation.ToFacet<Conversation, ConversationDto>();
        // Reported on the success path too, and not only on a refusal: the caller has to be able to
        // tell the user "your friend's phone will not be able to read this" at the moment it
        // happens. Discovering it later is discovering it as a bug.
        dto.UnreachableDevices = unreachableDevices;

        return Results.Ok(dto);
    }

    /// <summary>Which member devices got no Welcome.</summary>
    private static async Task<List<UnreachableDeviceDto>> ResolveUnreachableDevicesAsync(
        CreateConversationDto createDto, IMessageBus messageBus)
    {
        var memberIds = createDto.Members.Select(m => m.UserId).Distinct().ToList();
        if (memberIds.Count == 0) return [];

        GetUserDevicesResponse devices;
        try
        {
            devices = await messageBus.InvokeAsync<GetUserDevicesResponse>(
                new GetUserDevicesRequest { UserIds = memberIds });
        }
        catch (Exception)
        {
            return [];
        }

        var welcomed = createDto.DeviceWelcomes
            .Select(w => (w.UserId, w.DeviceId))
            .ToHashSet();

        return devices.Devices
            .Where(d => !welcomed.Contains((d.UserId, d.ClientDeviceId)))
            .Select(d => new UnreachableDeviceDto
            {
                UserId = d.UserId,
                DeviceId = d.ClientDeviceId,
                DeviceName = d.DeviceName,
            })
            .ToList();
    }

  

    /// <summary>Adds someone to a group conversation.</summary>
    [WolverinePost("/api/v1/conversations/{id}/members")]
    public async Task<(IResult, ConversationMemberAdded?)> AddConversationMember(
        string id,
        AddConversationMemberDto dto,
        [NotBody] IMessageBus messageBus,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return (Results.Unauthorized(), null);
        if (string.IsNullOrWhiteSpace(dto.UserId)) return (Results.BadRequest("UserId is required"), null);

        var conversation = await ctx.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (conversation is null) return (Results.NotFound(), null);
        if (conversation.Members.All(m => m.UserId != userId)) return (Results.Forbid(), null);

        if (conversation.Members.Count < 3 && string.IsNullOrWhiteSpace(conversation.Name))
            return (Results.BadRequest("Start a new group conversation instead of adding to a direct message."), null);

        if (conversation.Members.Any(m => m.UserId == dto.UserId))
            return (Results.Ok(conversation.ToFacet<Conversation, ConversationDto>()), null);

        if (!await IsBefriendedWithUsers(userId, [dto.UserId], messageBus))
            return (Results.BadRequest("User cannot be added to conversation if not friends"), null);

        var profileResponse = await messageBus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest
        {
            UserId = dto.UserId,
        });
        if (profileResponse.Profile is null) return (Results.BadRequest("Profile not found"), null);

        conversation.Members.Add(ConversationMember.Create(new CreateConversationMemberParams
        {
            UserId = dto.UserId,
            ConversationId = conversation.Id,
            PublicKey = Array.Empty<byte>(),
            CachedUserName = profileResponse.Profile.UserName,
            CachedUserHash = profileResponse.Profile.Hash,
        }));

        return (Results.Ok(conversation.ToFacet<Conversation, ConversationDto>()), new ConversationMemberAdded
        {
            ConversationId = conversation.Id,
            UserId = dto.UserId,
            CorrelationId = conversation.Id,
        });
    }

    /// <summary>Mute or unmute a DM/group conversation for the caller.</summary>
    [WolverinePut("/api/v1/conversations/{id}/notification-settings")]
    public async Task<IResult> UpdateNotificationSettings(string id, UpdateConversationNotificationDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var member = await ctx.Members.FirstOrDefaultAsync(m => m.ConversationId == id && m.UserId == userId);
        if (member is null) return Results.NotFound();

        member.MutedUntil = dto switch
        {
            { MuteForever: true } => new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero),
            { MuteMinutes: null } => member.MutedUntil,
            { MuteMinutes: <= 0 } => null,
            _ => DateTimeOffset.UtcNow.AddMinutes(dto.MuteMinutes!.Value),
        };
        member.UpdatedAt = DateTimeOffset.UtcNow;

        return Results.Ok(new { conversationId = id, mutedUntil = member.MutedUntil });
    }

    [WolverineDelete("/api/v1/conversations/{id}")]
    public async Task<IResult> DeleteConversation(string id, [NotBody] IMessageBus messageBus,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return Results.Unauthorized();
        
        var conversation = await ctx.Conversations.Include(conversation => conversation.Members).SingleAsync(c => c.Id == id);
        
        if(conversation.Members.All(m => m.UserId != userId)) return Results.Forbid();

        if (conversation.Members.Count == 1)
        {
            ctx.Conversations.Remove(conversation);
            // Intentionally not using cascading handlers, to make sure the conversation still exists at that point
            await messageBus.PublishAsync(new ConversationDeleted() { ConversationId = conversation.Id });  
            return Results.Ok();
        }
        
        conversation.Members.Remove(conversation.Members.Single(m => m.UserId == userId));

        await messageBus.PublishAsync(new ConversationMemberRemoved()
        {
            HasLeft = true,
            ConversationId = conversation.Id,
            UserId = userId
        });
        
        return Results.Ok();
    }


    private async Task<bool> IsBefriendedWithUsers(string ownUserId, List<string> userIds, IMessageBus messageBus)
    {
        
        var response = await messageBus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = ownUserId
        });
        if (response.Profile is null) return false;

        var memberProfiles = new List<ProfileDto>();

        foreach (var userId in userIds)
        {
            var memberResponse = await messageBus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
            {
                UserId = userId
            });
            
            if(memberResponse.Profile is null) return false;
            
            memberProfiles.Add(memberResponse.Profile);
        }
        
        var befriendedUserIds = response.Profile.Relationships
            .Where(r => r.Status == RelationshipStatus.Accepted)
            .Select(r => r.UserId).Where(u => u != ownUserId).ToList();
        
        // check if all user ids are friends 
        
        foreach (var getTokenUserId in userIds)
        {
            if(getTokenUserId == ownUserId) continue;
            // TODO: We have to check the users privacy bit settings here, but for now default to this
            if (!befriendedUserIds.Contains(getTokenUserId))
            {
                return false;

            }
        }

        return true;
    }
    
}