using System.Security.Claims;
using System.Text;
using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Http;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;
using MessagingAuthorIdType = Messaging.Contracts.Bus.Commands.AuthorIdType;

namespace Guild.Application.Endpoints;

public class InviteEndpoint
{
    /// <summary>Every invite a guild has, newest first.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/invites")]
    public async Task<IResult> GetInvitesAsync(string guildId, bool includeRevoked, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if(!await permissionService.CanUserPerformActionOnGuildAsync(userId: userId, guildId: guildId, Permissions.ManageGuild))
        {
            return Results.Forbid();
        }

        var query = ctx.GuildInvites.Include(g => g.Guild).Where(x => x.GuildId == guildId);
        if (!includeRevoked) query = query.Where(x => x.State != InviteState.Revoked);

        var invites = await query.ToListAsync();

        var now = DateTimeOffset.UtcNow;
        return Results.Ok(invites.Select(i => i.ToFacet<GuildInvite, InviteDto>().WithEffectiveState(i, now)).ToList());
    }

    [WolverinePost("/api/v1/guilds/{guildId}/invite")]

    public async Task<IResult> CreateInviteAsync(string guildId, CreateInviteDto createInviteDto, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildInviteAudienceService audienceService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();


        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.CreateInvite))
        {
            return Results.Forbid();
        }

        var guild = await ctx.Guilds.FindAsync(guildId);
        if (guild is null) return Results.NotFound();

        if (createInviteDto.MaxUses is <= 0)
            return Results.BadRequest("maxUses must be at least 1, or omitted for unlimited.");

        // Validated at creation rather than at redemption, because a target that cannot be honoured
        // is a link that will be shared and then disappoint somebody - and the person who can still
        // fix it is the one standing here now.
        if (createInviteDto.TargetType == InviteTargetType.VoiceChannel)
        {
            if (string.IsNullOrWhiteSpace(createInviteDto.ChannelId))
                return Results.BadRequest("A VoiceChannel invite must name the channel in channelId.");

            var targetType = await ctx.Channels.AsNoTracking()
                .Where(c => c.Id == createInviteDto.ChannelId && c.GuildId == guildId)
                .Select(c => (ChannelType?)c.Type)
                .FirstOrDefaultAsync();

            if (targetType is null)
                return Results.BadRequest("The target channel does not belong to this guild.");
            if (targetType != ChannelType.Voice)
                return Results.BadRequest("A VoiceChannel invite must target a voice channel.");
        }

        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = guildId,
            Type = createInviteDto.Type,
            ExpiresAt = createInviteDto.ExpiresAt,
            MaxUses = createInviteDto.MaxUses,
            ChannelId = createInviteDto.ChannelId,
            InviterId = userId,
            Temporary = createInviteDto.Temporary,
            TargetType = createInviteDto.TargetType,
            TargetUserId = createInviteDto.TargetUserId,
        });
        invite.Id = GuildInvite.GenerateId();
        invite.CreatedAt = DateTime.UtcNow;
        invite.UpdatedAt = DateTime.UtcNow;

        invite.Guild = guild;

        ctx.GuildInvites.Add(invite);

        auditLog.Log(guildId, userId, AuditActionType.InviteCreated, invite.Id,
            new { invite.Code, invite.Type, invite.ExpiresAt, invite.MaxUses, invite.ChannelId, invite.Temporary, invite.TargetType });

        await BroadcastCreatedAsync(invite, hub, audienceService, bus);

        return Results.Ok(invite.ToFacet<GuildInvite, InviteDto>().WithEffectiveState(invite, DateTimeOffset.UtcNow));
    }

    /// <summary>The realtime and bot half of minting an invite, shared with the vanity endpoint,
    /// which mints the backing invite through the same code so a vanity link is not a second kind of
    /// invite with a different event trail.</summary>
    internal static async Task BroadcastCreatedAsync(GuildInvite invite, IHubContext<EchoRealtimeHub> hub,
        GuildInviteAudienceService audienceService, IMessageBus bus)
    {
        // Not the usual whole-presence fan-out: the payload contains the code, and the code is the
        // credential. See GuildInviteAudienceService.
        var audience = await audienceService.ResolveAsync(invite.GuildId);
        if (audience.Count > 0)
        {
            await hub.Clients.Users(audience).SendAsync("guild.InviteCreated", new
            {
                GuildId = invite.GuildId,
                InviteId = invite.Id,
                Code = invite.Code,
                ChannelId = invite.ChannelId,
                InviterId = invite.InviterId,
                ExpiresAt = invite.ExpiresAt,
                MaxUses = invite.MaxUses,
                Uses = invite.UseCount,
                Temporary = invite.Temporary,
                TargetType = invite.TargetType.ToString(),
                TargetUserId = invite.TargetUserId,
            });
        }

        await bus.PublishAsync(new InviteCreatedForBots
        {
            GuildId = invite.GuildId,
            InviteId = invite.Id,
            Code = invite.Code,
            ChannelId = invite.ChannelId,
            InviterId = invite.InviterId,
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt,
            MaxUses = invite.MaxUses,
            Uses = invite.UseCount,
            Temporary = invite.Temporary,
            TargetType = invite.TargetType.ToString(),
            TargetUserId = invite.TargetUserId,
        });
    }

    /// <summary>The public preview.</summary>
    [WolverineGet("/api/v1/invites/{inviteId}")]
    public async Task<IResult> GetInviteAsync(string inviteId, [NotBody] MicroserviceContext ctx,
        [NotBody] HttpContext http, [NotBody] InvitePreviewRateLimiter limiter, [NotBody] VanityUrlService vanity)
    {
        if (!await limiter.TryTakeAsync(InvitePreviewRateLimiter.PartitionFor(http)))
            return TooManyRequests();

        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Id == inviteId);
        if (invite == null)
        {
            invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Code == inviteId);
        }
        invite ??= await vanity.ResolveInviteAsync(inviteId);

        if(invite is null) return Results.NotFound();
        if (invite.State == InviteState.Revoked) return Results.NotFound();

        return Results.Ok(await WithWelcomeScreenAsync(invite, ctx));
    }

    [WolverineGet("/api/v1/invites/code/{code}")]
    public async Task<IResult> GetInviteByCodeAsync(string code, [NotBody] MicroserviceContext ctx,
        [NotBody] HttpContext http, [NotBody] InvitePreviewRateLimiter limiter)
    {
        if (!await limiter.TryTakeAsync(InvitePreviewRateLimiter.PartitionFor(http)))
            return TooManyRequests();

        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Code == code);
        if(invite is null) return Results.NotFound();
        if (invite.State == InviteState.Revoked) return Results.NotFound();

        return Results.Ok(await WithWelcomeScreenAsync(invite, ctx));
    }

    /// <summary>
    /// The vanity half of the preview, so a slug reaches exactly the object a code does.
    /// </summary>
    [WolverineGet("/api/v1/invites/vanity/{slug}")]
    public async Task<IResult> GetInviteByVanityAsync(string slug, [NotBody] MicroserviceContext ctx,
        [NotBody] HttpContext http, [NotBody] InvitePreviewRateLimiter limiter, [NotBody] VanityUrlService vanity)
    {
        if (!await limiter.TryTakeAsync(InvitePreviewRateLimiter.PartitionFor(http)))
            return TooManyRequests();

        var invite = await vanity.ResolveInviteAsync(slug);
        if (invite is null || invite.State == InviteState.Revoked) return Results.NotFound();

        return Results.Ok(await WithWelcomeScreenAsync(invite, ctx));
    }

    private static IResult TooManyRequests() =>
        Results.Json(new { error = "rate_limited", message = "Too many invite lookups; try again shortly." },
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>The invite preview is the only place a not-yet-member can see the guild's welcome
    /// splash, which is the whole point of the feature - so it is attached here rather than left to
    /// the membership-gated welcome-screen endpoint.</summary>
    private static async Task<InviteDto> WithWelcomeScreenAsync(GuildInvite invite, MicroserviceContext ctx)
    {
        var dto = invite.ToFacet<GuildInvite, InviteDto>().WithEffectiveState(invite, DateTimeOffset.UtcNow);
        dto.WelcomeScreen = await WelcomeScreenEndpoint.LoadAsync(ctx, invite.GuildId, enabledOnly: true);
        return dto;
    }

    /// <summary>Withdraws an invite.</summary>
    [WolverineDelete("/api/v1/invites/{inviteId}")]
    public async Task<IResult> DeleteInviteAsync(string inviteId, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildInviteAudienceService audienceService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Id == inviteId);

        if(invite is null) return Results.NotFound();

        var authorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, invite.GuildId, Permissions.ManageGuild)
            || (invite.ChannelId is not null
                && await permissionService.CanUserPerformActionAsync(userId, invite.ChannelId, Permissions.ManageChannel));

        if(!authorized) return Results.Forbid();

        // Revoking an already-revoked invite is the same answer as revoking it the first time.
        if (invite.State != InviteState.Revoked)
        {
            invite.Revoke(DateTimeOffset.UtcNow);

            auditLog.Log(invite.GuildId, userId, AuditActionType.InviteDeleted, invite.Id,
                new { invite.Code, invite.UseCount });

            var audience = await audienceService.ResolveAsync(invite.GuildId);
            if (audience.Count > 0)
            {
                await hub.Clients.Users(audience).SendAsync("guild.InviteDeleted",
                    new { GuildId = invite.GuildId, InviteId = invite.Id, Code = invite.Code, ChannelId = invite.ChannelId });
            }

            await bus.PublishAsync(new InviteDeletedForBots
            {
                GuildId = invite.GuildId,
                InviteId = invite.Id,
                Code = invite.Code,
                ChannelId = invite.ChannelId,
            });
        }

        return Results.Ok(invite.ToFacet<GuildInvite, InviteDto>().WithEffectiveState(invite, DateTimeOffset.UtcNow));
    }

    [WolverinePost("/api/v1/invites/{inviteId}/redeem")]
    public async Task<IResult> RedeemInviteAsync(string inviteId, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache, [NotBody] IMessageBus bus,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] VanityUrlService vanity)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var profileResponse = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = userId
        });

        if(profileResponse.Profile is null) return Results.BadRequest("User not found");
        var searchValue = profileResponse.Profile.UserName! + "#" + profileResponse.Profile.Hash;

        var invite = await ctx.GuildInvites.FirstOrDefaultAsync(i => i.Id == inviteId);

        if (invite == null)
        {
            invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Code == inviteId);
        }

        // Same three-step resolution as the preview, so a link that previews is a link that
        // redeems. Tracked rather than AsNoTracking, because UseCount is about to be incremented.
        invite ??= await vanity.ResolveInviteAsync(inviteId, track: true);

        if (invite is null) return Results.NotFound();

        // One predicate for every reason an invite is finished, replacing the three that had
        // drifted apart from what the read paths reported.
        if (invite.State == InviteState.Revoked) return Results.NotFound();
        if (!invite.IsRedeemable(DateTimeOffset.UtcNow))
            return Results.BadRequest(invite.IsExhausted()
                ? "Invite has reached its maximum number of uses"
                : "Invite has expired");

        var isBanned = await ctx.Set<GuildBan>().AnyAsync(b => b.GuildId == invite.GuildId && b.BannedUserId == userId);
        if (isBanned) return Results.Forbid();

        // Redeeming while already a member is a no-op, not a second membership.
        var alreadyMember = await ctx.GuildMembers
            .AnyAsync(m => m.GuildId == invite.GuildId && m.UserId == userId);
        if (alreadyMember) return Results.Conflict("User is already a member of this guild.");

        var guild = await ctx.Guilds.Include(guild => guild.Channels).Include(guild => guild.Roles).FirstOrDefaultAsync(g => g.Id == invite.GuildId);
        if(guild is null) return Results.NotFound();

        if (guild.VerificationLevel != GuildVerificationLevel.None)
        {
            var userResponse = await bus.InvokeAsync<GetUserByIdResponse>(new GetUserByIdRequest { UserId = userId });
            var identityUser = userResponse.User;
            if (identityUser is null) return Results.BadRequest("User not found");

            var accountAge = DateTimeOffset.UtcNow - identityUser.CreatedAt;
            var meetsRequirement = guild.VerificationLevel.MeetsRequirement(identityUser.EmailConfirmed, accountAge);

            if (!meetsRequirement)
            {
                return Results.Json(new { error = "verification_level_not_met", requiredLevel = guild.VerificationLevel.ToString() },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var onboardingConfig = await ctx.Set<GuildOnboardingConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.GuildId == guild.Id);
        var onboardingRequired = onboardingConfig is { Enabled: true } && !string.IsNullOrWhiteSpace(onboardingConfig.RulesText);

        var id = GuildMember.GenerateId();
        var joinedAt = DateTime.UtcNow;
        var member = new GuildMember()
        {
            Id = id,
            CreatedAt = joinedAt,
            UpdatedAt = joinedAt,
            GuildId = guild.Id,
            UserId = userId,
            JoinedAt = joinedAt,
            InviteId = invite.Id,
            InviteCode = invite.Code,
            // Snapshotted, not read back through the FK - see GuildMember.TemporaryMembership.
            TemporaryMembership = invite.Temporary,
            Nickname = profileResponse.Profile.UserName,
            SearchValue = searchValue.ToUpperInvariant(),
            // Only members who join while onboarding is actually configured are gated by it -
            // everyone else is auto-completed at join time, same as today's ungated join.
            OnboardingCompletedAt = onboardingRequired ? null : joinedAt,
        };

        // A guild with no @everyone role is malformed rather than impossible - imports and template
        // instantiation both build the role set themselves.
        var role = guild.Roles.FirstOrDefault(r => r.Type == RoleType.Everyone);
        if (role is null)
            return Results.Problem("Guild is missing its @everyone role; cannot complete the join.",
                statusCode: StatusCodes.Status500InternalServerError);

        // Spent here, once nothing above can still refuse. AutoApplyTransactions commits on a
        // normal return without looking at the IResult, so an earlier increment burned a one-time
        // invite for everyone else on a redemption that was rejected.
        invite.UseCount++;
        if(invite.Type == InviteType.OneTime || invite.IsExhausted()) invite.State = InviteState.Expired;

        ctx.GuildMembers.Add(member);

        role.Members.Add(new RoleMember()
        {
            Id = RoleMember.GenerateId(),
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            MemberId = member.Id,
            RoleId = role.Id,
        });

        // Committed here rather than left to the transactional middleware at the end of the
        // handler, because everything below this line can cause this account's permissions to be
        // resolved - and resolving them while the GuildMember row is still uncommitted takes the
        // fail-closed non-member branch in ComputePermissionsForUserAsync, which caches "holds
        // nothing" for 15 minutes.
        await ctx.SaveChangesAsync();

        var cacheKeyOne = GuildPermissionsForUser.GetCacheKey(guild.Id, userId);
        await cache.RemoveAsync(cacheKeyOne);

        // Per-channel entries are keyed but never written - ComputePermissionsForUserAsync caches
        // the whole resolved set under the key above - so this clears nothing today.
        foreach (var channel in guild.Channels)
        {
            var cacheKey = GuildChannelPermission.GetCacheKey(guild.Id, channel.Id, userId);
            await cache.RemoveAsync(cacheKey);
        }

        // Previously nothing broadcast a join at all - MemberBanned/Kicked/Left all notify
        // realtime, but a join never did, for either the human client or bots.
        var presence = await guildHydrateService.GetGuildPresenceAsync(guild.Id);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.MemberJoined", new { GuildId = guild.Id, UserId = userId });

        await bus.PublishAsync(new MemberJoinedForBots { GuildId = guild.Id, UserId = userId });

        // Discord-style system message in the guild's system channel.
        if (!string.IsNullOrWhiteSpace(guild.SystemChannelId))
        {
            await bus.InvokeAsync(new CreateMessageCommand()
            {
                Content = Encoding.UTF8.GetBytes($"{profileResponse.Profile.UserName} joined the server"),
                ChannelId = guild.SystemChannelId,
                AuthorId = userId,
                AuthorIdType = MessagingAuthorIdType.User,
                Mentions = [],
                Type = MessagingMessageType.GuildMemberJoin,
            });
        }

        // A voice target only survives as far as the channel does.
        var voiceTargetLives = invite.TargetType == InviteTargetType.VoiceChannel
            && invite.ChannelId is not null
            && guild.Channels.Any(c => c.Id == invite.ChannelId && c.Type == ChannelType.Voice);

        // Still 202, now with a body.
        return Results.Accepted(value: new RedeemInviteResultDto
        {
            GuildId = guild.Id,
            ChannelId = invite.ChannelId,
            TargetType = invite.TargetType,
            TargetUserId = invite.TargetUserId,
            JoinVoice = voiceTargetLives,
            OnboardingRequired = onboardingRequired,
            TemporaryMembership = invite.Temporary,
        });

    }
}
