using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Domain;
using Messaging.Contracts.Bus.Commands;
using Echo.Realtime.Devices;
using Facet.Extensions;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
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
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Messaging.Application.Endpoints;

[Authorize]
public class ConversationEndpoints
{
    
    
    /// <summary>How long a consume-tokens answer is replayed rather than re-consumed.</summary>
    public static readonly TimeSpan ConsumeTokenReplayWindow = TimeSpan.FromMinutes(2);

    /// <summary>Distinct consume calls one caller may make inside the replay window.</summary>
    public const int MaxConsumeCallsPerWindow = 20;

    /// <summary>
    /// How many times one caller may draw from a given target's devices per <see
    /// cref="ConsumeTokenPerTargetWindow"/>.
    /// </summary>
    public const int MaxConsumePerTargetPerWindow = 10;

    public static readonly TimeSpan ConsumeTokenPerTargetWindow = TimeSpan.FromHours(24);

    /// <summary>A counter that expires a fixed interval after its first increment.</summary>
    private readonly record struct WindowedCount(int Count, DateTimeOffset WindowStart)
    {
        public static WindowedCount Parse(string? raw, DateTimeOffset now)
        {
            // `count:windowStartUnixSeconds`.
            if (string.IsNullOrEmpty(raw)) return new WindowedCount(0, now);

            var separator = raw.IndexOf(':');
            if (separator < 0)
                return new WindowedCount(int.TryParse(raw, out var legacy) ? legacy : 0, now);

            if (!int.TryParse(raw.AsSpan(0, separator), out var count)
                || !long.TryParse(raw.AsSpan(separator + 1), out var startedAt))
            {
                return new WindowedCount(0, now);
            }

            return new WindowedCount(count, DateTimeOffset.FromUnixTimeSeconds(startedAt));
        }

        public string Serialize() => $"{Count}:{WindowStart.ToUnixTimeSeconds()}";

        /// <summary>What is left of the window, never zero or negative - a non-positive TTL would
        /// make the write a no-op and silently uncap the counter.</summary>
        public TimeSpan RemainingAt(DateTimeOffset now, TimeSpan window)
        {
            var remaining = WindowStart + window - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        }
    }

    /// <summary>Cap on one call's target list, which was unbounded - and every entry costs a package
    /// from every device that user owns.</summary>
    public const int MaxConsumeTargets = 200;

    [WolverinePost("/api/v1/conversations/consume-tokens")]
    public async Task<IResult> FetchTokensForUsers(ConsumeMlsDeviceTokensForUserRequest request,
        [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache, [NotBody] DirectMessagePolicyService dmPolicy)
    {
        var ownUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(ownUserId is null) return Results.Unauthorized();

        var targets = request.UserIds.Distinct().OrderBy(u => u, StringComparer.Ordinal).ToList();

        if (targets.Count > MaxConsumeTargets)
        {
            return Results.BadRequest(
                $"At most {MaxConsumeTargets} users per call; each one costs a single-use key package "
                + "from every device they own.");
        }

        // T0-2.
        var refusal = await dmPolicy.EvaluateAsync(ownUserId, targets);
        if (refusal is not null) return DmRefusalResults.ToResult(refusal);

        var replayKey = $"mls-consume:{ownUserId}:{string.Join(",", targets)}";

        // Idempotent inside the window.
        var cached = await cache.GetStringAsync(replayKey);
        if (cached is not null)
        {
            var replayed = JsonSerializer.Deserialize<ConsumeMlsDeviceTokensForUserResponse>(cached);
            if (replayed is not null) return Results.Ok(replayed);
        }

        var now = DateTimeOffset.UtcNow;

        var budgetKey = $"mls-consume-budget:{ownUserId}";
        var budget = WindowedCount.Parse(await cache.GetStringAsync(budgetKey), now);
        if (budget.Count >= MaxConsumeCallsPerWindow)
        {
            return Results.Json(
                new
                {
                    retryAfterSeconds =
                        (int)budget.RemainingAt(now, ConsumeTokenReplayWindow).TotalSeconds,
                },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Per-target draw, checked before anything is consumed so a call that would exceed the
        // budget for one member of the list does not burn packages from the rest.
        var perTargetKeys = targets.Select(t => $"mls-consume-target:{ownUserId}:{t}").ToList();
        var perTargetSpend = new WindowedCount[targets.Count];

        for (var i = 0; i < targets.Count; i++)
        {
            perTargetSpend[i] = WindowedCount.Parse(await cache.GetStringAsync(perTargetKeys[i]), now);

            if (perTargetSpend[i].Count >= MaxConsumePerTargetPerWindow)
            {
                return Results.Json(
                    new
                    {
                        error = "key_package_budget_exhausted",
                        userId = targets[i],
                        // The real remainder of *this* target's window, not the window length: the
                        // budget is spent from the first draw, so a caller told to come back in 24
                        // hours when 3 remain would simply be wrong.
                        retryAfterSeconds =
                            (int)perTargetSpend[i].RemainingAt(now, ConsumeTokenPerTargetWindow).TotalSeconds,
                    },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        var tokens = await messageBus.InvokeAsync<ConsumeMlsDeviceTokensForUserResponse>(request);

        await cache.SetStringAsync(replayKey, JsonSerializer.Serialize(tokens),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ConsumeTokenReplayWindow });

        var nextBudget = budget with { Count = budget.Count + 1 };
        await cache.SetStringAsync(budgetKey, nextBudget.Serialize(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = nextBudget.RemainingAt(now, ConsumeTokenReplayWindow),
        });

        for (var i = 0; i < targets.Count; i++)
        {
            var next = perTargetSpend[i] with { Count = perTargetSpend[i].Count + 1 };
            await cache.SetStringAsync(perTargetKeys[i], next.Serialize(), new DistributedCacheEntryOptions
            {
                // Measured from the window's start, so the tenth draw expires at the same instant the
                // first one set - the counter is a budget, not a rate.
                AbsoluteExpirationRelativeToNow = next.RemainingAt(now, ConsumeTokenPerTargetWindow),
            });
        }

        return Results.Ok(tokens);
    }
    
    [WolverinePost( "/api/v1/conversations")]
    public async Task<IResult> CreateConversation(CreateConversationDto createDto,
        [FromQuery] bool allowPartialDeviceCoverage,
        [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] MlsDeviceCoverageService coverage, [NotBody] HttpContext http,
        [NotBody] DirectMessagePolicyService dmPolicy )
    {


        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return Results.Unauthorized();

        // A retried or double-tapped create must land in the room the caller is already in, not beside
        // it. Checked before the profile and policy round trips: the caller is a member of whatever
        // this matches, so there is no first-contact decision left to make.
        var duplicate = await FindEquivalentConversationAsync(createDto, userId, ctx);
        if (duplicate is not null)
        {
            // Relative on purpose. The gateway rewrites /api/v1/messaging/{**} to /api/v1/{**} and
            // forwards no prefix header, so the service cannot name its own public path; resolved
            // against either base, this lands on the single-conversation GET.
            http.Response.Headers.Location = $"conversations/{duplicate.Id}";

            // 302 rather than 200 so a client can tell nothing was created. Nothing re-POSTs here:
            // only 307 and 308 preserve the method across a redirect.
            return Results.Json(duplicate.ToFacet<Conversation, ConversationDto>(),
                statusCode: StatusCodes.Status302Found);
        }

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
        
        // T0-2.
        var refusal = await dmPolicy.EvaluateAsync(
            userId,
            createDto.Members.Select(m => m.UserId).ToList(),
            memberProfiles.DistinctBy(p => p.UserId, StringComparer.Ordinal)
                .ToDictionary(p => p.UserId, p => p, StringComparer.Ordinal));

        if (refusal is not null) return DmRefusalResults.ToResult(refusal);


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
            unreachableDevices = await ResolveUnreachableDevicesAsync(createDto, userId, coverage, http);

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
                // The device holding the group has no Welcome addressed to it, so without this every
                // later coverage read reports the creating device as unable to read the conversation
                // it just created. See MlsGroupGeneration.ActivatedByDeviceId.
                ActivatedByDeviceId = CreatingDeviceId(http),
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

    /// <summary>The local conversation this request would duplicate - same member set, same name,
    /// same encryption state - or null when there is none.</summary>
    private static async Task<Conversation?> FindEquivalentConversationAsync(
        CreateConversationDto createDto, string callerUserId, MicroserviceContext ctx)
    {
        var memberIds = createDto.Members
            .Select(m => m.UserId)
            .Append(callerUserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var query = ctx.Conversations
            .AsNoTracking()
            .Include(c => c.Members)
            .Where(c => c.OriginInstanceId == null
                        && c.EncryptionState == createDto.Encryption
                        && c.Members.Count == memberIds.Count);

        var name = string.IsNullOrWhiteSpace(createDto.Name) ? null : createDto.Name;
        query = name is null
            ? query.Where(c => c.Name == null || c.Name == "")
            : query.Where(c => c.Name == name);

        // One Any per member: a set comparison would not translate, and the count above closes it
        // into exact equality.
        foreach (var memberId in memberIds)
            query = query.Where(c => c.Members.Any(m => m.UserId == memberId));

        // The liveliest of them, matching DirectConversationResolver, because rows predating this
        // check can already have messages in them.
        return await query
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>Which participant devices got no Welcome.</summary>
    private static async Task<List<UnreachableDeviceDto>> ResolveUnreachableDevicesAsync(
        CreateConversationDto createDto,
        string callerUserId,
        MlsDeviceCoverageService coverage,
        HttpContext http)
    {
        var participantIds = createDto.Members.Select(m => m.UserId).Distinct().ToList();

        var covered = createDto.DeviceWelcomes
            .Select(w => (w.UserId, w.DeviceId))
            .ToHashSet();

        if (CreatingDeviceId(http) is { } creatingDeviceId)
        {
            participantIds.Add(callerUserId);
            covered.Add((callerUserId, creatingDeviceId));
        }

        if (participantIds.Count == 0) return [];

        return await coverage.ResolveAsync("new conversation", participantIds, covered);
    }

    /// <summary>The caller's device from <c>X-Device-Id</c>, or null when the client sent none.</summary>
    private static string? CreatingDeviceId(HttpContext http)
    {
        var header = http.Request.Headers[DeviceIdentity.HeaderName].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }

  

    /// <summary>Adds someone to a group conversation.</summary>
    [WolverinePost("/api/v1/conversations/{id}/members")]
    public async Task<(IResult, ConversationMemberAdded?)> AddConversationMember(
        string id,
        AddConversationMemberDto dto,
        [NotBody] IMessageBus messageBus,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx,
        [NotBody] DirectMessagePolicyService dmPolicy,
        [NotBody] BlockCache blocks)
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

        var refusal = await dmPolicy.EvaluateAsync(userId, [dto.UserId]);
        if (refusal is not null) return (DmRefusalResults.ToResult(refusal), null);

        // The candidate against everyone already in the room.
        var existingMemberIds = conversation.Members
            .Select(m => m.UserId)
            .Where(u => !string.Equals(u, userId, StringComparison.Ordinal))
            .ToList();

        if (existingMemberIds.Count > 0)
        {
            var candidateBlocks = await blocks.BlockedEitherWayAsync(dto.UserId, existingMemberIds);
            if (candidateBlocks.Count > 0)
                return (DmRefusalResults.ToResult(new DmRefusal(DmRefusal.Blocked, dto.UserId)), null);
        }

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

    /// <summary>Longest group name accepted, matching the client's own counter.</summary>
    public const int MaxConversationNameLength = 100;

    /// <summary>Renames a group conversation, or clears the name so the member list titles it again.</summary>
    [WolverinePatch("/api/v1/conversations/{id}")]
    public static async Task<(IResult, ConversationUpdated?)> UpdateConversation(
        string id,
        UpdateConversationDto dto,
        [NotBody] IMessageBus messageBus,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return (Results.Unauthorized(), null);

        var conversation = await ctx.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (conversation is null) return (Results.NotFound(), null);
        if (conversation.Members.All(m => m.UserId != userId)) return (Results.Forbid(), null);
        if (conversation.Members.Count <= 2)
            return (Results.BadRequest("Only a group conversation can be renamed."), null);

        var name = string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim();
        if (name is { Length: > MaxConversationNameLength })
            return (Results.BadRequest($"A group name is at most {MaxConversationNameLength} characters."), null);

        if (name == conversation.Name)
            return (Results.Ok(conversation.ToFacet<Conversation, ConversationDto>()), null);

        conversation.Name = name;
        conversation.UpdatedAt = DateTime.UtcNow;

        await WriteSystemMessageAsync(messageBus, conversation.Id, userId,
            ContractMessageType.GroupNameChanged, name ?? string.Empty);

        return (Results.Ok(conversation.ToFacet<Conversation, ConversationDto>()), new ConversationUpdated
        {
            ConversationId = conversation.Id,
            CorrelationId = conversation.Id,
            Name = conversation.Name,
            IconUpdatedAt = conversation.IconUpdatedAt,
        });
    }

    /// <summary>Leaves a notice in the group's own history, the way the call entries do.</summary>
    internal static Task WriteSystemMessageAsync(
        IMessageBus messageBus, string conversationId, string authorId,
        ContractMessageType type, string content) =>
        messageBus.InvokeAsync(new CreateMessageCommand
        {
            ConversationId = conversationId,
            AuthorId = authorId,
            AuthorIdType = Contracts.Bus.Commands.AuthorIdType.User,
            Type = type,
            Content = Encoding.UTF8.GetBytes(content),
            Mentions = [],
        });

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


    // IsBefriendedWithUsers is gone.
}
