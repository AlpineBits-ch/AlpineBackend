using System.Security.Claims;
using Facet.Extensions;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
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
    
    
    [WolverinePost("/api/v1/conversations/consume-tokens")]
    public async Task<IResult> FetchTokensForUsers(ConsumeMlsDeviceTokensForUserRequest request, [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx )
    {
        var ownUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(ownUserId is null) return Results.Unauthorized();


        if (!await IsBefriendedWithUsers(ownUserId, request.UserIds.ToList(), messageBus))
        {
            return Results.BadRequest("User is not friends with the users you are trying to consume tokens for");
        }
        
        var tokens = await messageBus.InvokeAsync<ConsumeMlsDeviceTokensForUserResponse>(request);
        
        return Results.Ok(tokens);
    }
    
    [WolverinePost( "/api/v1/conversations")]
    public async Task<IResult> CreateConversation(CreateConversationDto createDto, [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx )
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


        // No token consumption here. The client already consumed one key package per invitee device
        // via /consume-tokens - that is what it built the group and the Welcomes from. Consuming a
        // second set on its behalf burned a package per device per conversation and handed back
        // packages nobody held the init key for; the result was discarded anyway.
        if (createDto.Encryption == ChannelEncryptionState.Encrypted)
        {
            // A member with no Welcome is a member who can never read the conversation: their
            // device had no key package left when the client consumed, so it was never added to the
            // group. Silently creating a conversation that is permanently unreadable for someone in
            // it is worse than refusing to create it.
            var welcomedUserIds = createDto.DeviceWelcomes
                .Select(w => w.UserId)
                .ToHashSet();

            var unreachable = createDto.Members
                .Select(m => m.UserId)
                .Where(id => !welcomedUserIds.Contains(id))
                .ToList();

            if (unreachable.Count > 0)
            {
                return Results.BadRequest(
                    "No MLS key packages were available for these members, so they cannot be added " +
                    $"to an encrypted conversation: {string.Join(", ", unreachable)}");
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

        // A conversation created encrypted starts on generation 1. Without this row the context
        // reads as plaintext everywhere that matters - the send path would refuse the very
        // ciphertext this client is about to produce, and no commit could ever be published against
        // the group it just built.
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
        
        
        return Results.Ok(conversation.ToFacet<Conversation, ConversationDto>());
    }

  

    /// <summary>Mute or unmute a DM/group conversation for the caller. Only a mute, no level -
    /// see ConversationMember.MutedUntil for why "only mentions" has no meaning in a DM.</summary>
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