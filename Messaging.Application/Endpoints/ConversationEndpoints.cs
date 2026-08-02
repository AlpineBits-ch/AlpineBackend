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
    
    
    /// <summary>How long a consume-tokens answer is replayed rather than re-consumed.
    ///
    /// <para>Every call burns one single-use key package per target device, and the call is not
    /// otherwise limited - so one friend could drain roughly a hundred packages a minute from every
    /// device you own, forcing everybody onto the reusable last-resort package (losing forward
    /// secrecy on every subsequent join) and then onto Unreachable. Replaying the same answer inside
    /// this window makes a retried or duplicated request free, which is also what a client that lost
    /// the response actually needs.</para></summary>
    public static readonly TimeSpan ConsumeTokenReplayWindow = TimeSpan.FromMinutes(2);

    /// <summary>Distinct consume calls one caller may make inside the replay window. Well above what
    /// creating a conversation needs - one call per creation - and far below a drain.</summary>
    public const int MaxConsumeCallsPerWindow = 20;

    /// <summary>
    /// How many times one caller may draw from a given target's devices per
    /// <see cref="ConsumeTokenPerTargetWindow"/>.
    ///
    /// <para><b>The short window was the wrong unit.</b> Twenty calls per two minutes is six hundred
    /// packages an hour out of a stock of a hundred - so a single friend could empty every device
    /// you own inside ten minutes, forcing every subsequent join onto the reusable last-resort
    /// package (no forward secrecy from that point back) and then, on a <c>VerifiedDevices</c>
    /// account which has no such fallback, into <c>Unreachable</c>: the victim can no longer be added
    /// to any new conversation at all. Rate is not the right thing to bound here; total draw against
    /// a particular person is.</para>
    ///
    /// <para>Ten a day is far more than adding someone to conversations needs, and takes a tenth of
    /// one device's stock.</para>
    /// </summary>
    public const int MaxConsumePerTargetPerWindow = 10;

    public static readonly TimeSpan ConsumeTokenPerTargetWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// A counter that expires a fixed interval after its <i>first</i> increment.
    ///
    /// <para><b>Writing the count back with a fresh
    /// <see cref="DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow"/> does not do this.</b>
    /// It restarts the clock on every draw, which turns "ten per day" into "ten per any twenty-four
    /// hours of silence" - so a caller who keeps drawing never lets the counter expire, but a caller
    /// who paces themselves just under the limit resets it forever and the cap becomes a rate, not a
    /// budget. Since the whole point of the per-target rule (H7) is to bound the <i>total</i> draw
    /// against one person rather than its rate, that is the wrong shape.</para>
    ///
    /// <para>So the window start travels in the value and the TTL is always measured from it. The
    /// entry still expires on its own, which is what keeps this a cache entry and not a table.</para>
    /// </summary>
    private readonly record struct WindowedCount(int Count, DateTimeOffset WindowStart)
    {
        public static WindowedCount Parse(string? raw, DateTimeOffset now)
        {
            // `count:windowStartUnixSeconds`. A value written by an older build is just a number; it
            // is read as a count whose window starts now, which at worst grants one extra window.
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
        [NotBody] IDistributedCache cache)
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

        if (!await IsBefriendedWithUsers(ownUserId, request.UserIds.ToList(), messageBus))
        {
            return Results.BadRequest("User is not friends with the users you are trying to consume tokens for");
        }

        var replayKey = $"mls-consume:{ownUserId}:{string.Join(",", targets)}";

        // Idempotent inside the window. A client that retried because it never saw the response gets
        // the same packages back instead of burning a second set - which used to leave the first set
        // consumed and held by nobody, i.e. devices the server believed were in a group they had no
        // init key for.
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


        // No token consumption here. The client already consumed one key package per invitee device
        // via /consume-tokens - that is what it built the group and the Welcomes from. Consuming a
        // second set on its behalf burned a package per device per conversation and handed back
        // packages nobody held the init key for; the result was discarded anyway.
        var unreachableDevices = new List<UnreachableDeviceDto>();

        if (createDto.Encryption == ChannelEncryptionState.Encrypted)
        {
            // Per *device*, not per user. A member is reachable only if every one of their devices
            // got a Welcome: an MLS group member is a device, and a user whose laptop was welcomed
            // and whose phone was not is a user who reads the conversation on one machine and sees
            // undecryptable noise on the other - with nothing anywhere saying why. The old check
            // asked only whether the user appeared at all, so the phone was silently dropped.
            unreachableDevices = await ResolveUnreachableDevicesAsync(createDto, messageBus);

            // Permissive by default during the rollout. Clients in the field cannot pass the
            // override flag, so refusing here would take away their ability to create an encrypted
            // conversation at all - a worse outcome than a partially covered one they are now told
            // about. The list is returned either way, so the flip to refusing is a policy change
            // rather than a code change.
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


        var dto = conversation.ToFacet<Conversation, ConversationDto>();
        // Reported on the success path too, and not only on a refusal: the caller has to be able to
        // tell the user "your friend's phone will not be able to read this" at the moment it
        // happens. Discovering it later is discovering it as a bug.
        dto.UnreachableDevices = unreachableDevices;

        return Results.Ok(dto);
    }

    /// <summary>
    /// Which member devices got no Welcome.
    ///
    /// <para>Asks Identity for each member's active devices and subtracts the ones the caller
    /// actually sealed a Welcome to. If Identity cannot answer, the list comes back empty rather
    /// than blocking creation - a degraded reachability report is a far better failure than being
    /// unable to start a conversation because a sibling service is down.</para>
    /// </summary>
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

  

    /// <summary>
    /// Adds someone to a group conversation.
    ///
    /// <para>Same friendship rule as creation: you may only pull in people you are actually friends
    /// with, so a group cannot be used to put a stranger in front of someone.</para>
    ///
    /// <para>Refused on a two-person DM. Growing a 1:1 into a group silently changes what the other
    /// person thought they were in - that has to be an explicit new conversation, not a side effect
    /// of someone else's action.</para>
    ///
    /// <para>For an encrypted conversation this only adds them to the roster; their devices still
    /// have to be admitted to the MLS group, which only a member's client can do. Until that commit
    /// lands they are a member who cannot read - visible to them, since they see the conversation
    /// but no history.</para>
    /// </summary>
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