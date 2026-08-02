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

namespace Guild.Application.Endpoints;

public class InviteEndpoint
{


    [WolverineGet("/api/v1/guilds/{guildId}/invites")]
    public async Task<IResult> GetInvitesAsync(string guildId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        
        if(!await permissionService.CanUserPerformActionOnGuildAsync(userId: userId, guildId: guildId, Permissions.ManageChannel))
        {
            return Results.Forbid();
        }
        
        var invites = await ctx.GuildInvites.Include(g => g.Guild).Where(x => x.GuildId == guildId).ToListAsync();
        return Results.Ok(invites.SelectFacets<GuildInvite, InviteDto>());
    }
    
    [WolverinePost("/api/v1/guilds/{guildId}/invite")]

    public async Task<IResult> CreateInviteAsync(string guildId, CreateInviteDto createInviteDto, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();


        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.CreateInvite))
        {
            return Results.Forbid();
        }
        
        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = guildId,
            Type = createInviteDto.Type,
            ExpiresAt = createInviteDto.ExpiresAt,
            MaxUses = createInviteDto.MaxUses,
            ChannelId = createInviteDto.ChannelId,
        });
        invite.Id = GuildInvite.GenerateId();
        invite.CreatedAt = DateTime.UtcNow;
        invite.UpdatedAt = DateTime.UtcNow;

        var guild = await ctx.Guilds.FindAsync(guildId);
        if (guild is null) return Results.NotFound();
        invite.Guild = guild;

        ctx.GuildInvites.Add(invite);

        auditLog.Log(guildId, userId, AuditActionType.InviteCreated, invite.Id,
            new { invite.Code, invite.Type, invite.ExpiresAt, invite.MaxUses, invite.ChannelId });

        return Results.Ok(invite.ToFacet<GuildInvite, InviteDto>());
    }


    [WolverineGet("/api/v1/invites/{inviteId}")]
    public async Task<IResult> GetInviteAsync(string inviteId, [NotBody] MicroserviceContext ctx)
    {
        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Id == inviteId);
        if (invite == null)
        {
            invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Code == inviteId);
        }
        if(invite is null) return Results.NotFound();
        return Results.Ok(await WithWelcomeScreenAsync(invite, ctx));
    }

    [WolverineGet("/api/v1/invites/code/{code}")]
    public async Task<IResult> GetInviteByCodeAsync(string code, [NotBody] MicroserviceContext ctx)
    {
        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Code == code);
        if(invite is null) return Results.NotFound();
        return Results.Ok(await WithWelcomeScreenAsync(invite, ctx));
    }

    /// <summary>The invite preview is the only place a not-yet-member can see the guild's welcome
    /// splash, which is the whole point of the feature - so it is attached here rather than left to
    /// the membership-gated welcome-screen endpoint.</summary>
    private static async Task<InviteDto> WithWelcomeScreenAsync(GuildInvite invite, MicroserviceContext ctx)
    {
        var dto = invite.ToFacet<GuildInvite, InviteDto>();
        dto.WelcomeScreen = await WelcomeScreenEndpoint.LoadAsync(ctx, invite.GuildId, enabledOnly: true);
        return dto;
    }
    [WolverineDelete("/api/v1/invites/{inviteId}")]
    public async Task<IResult> DeleteInviteAsync(string inviteId, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Id == inviteId);

        if(invite is null) return Results.NotFound();

        if(!await permissionService.CanUserPerformActionOnGuildAsync(userId, invite.GuildId, Permissions.ManageChannel))
            return Results.Forbid();

        ctx.GuildInvites.Remove(invite);

        auditLog.Log(invite.GuildId, userId, AuditActionType.InviteDeleted, invite.Id,
            new { invite.Code, invite.UseCount });

        return Results.Ok(invite.ToFacet<GuildInvite, InviteDto>());
    }

    [WolverinePost("/api/v1/invites/{inviteId}/redeem")]
    public async Task<IResult> RedeemInviteAsync(string inviteId, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache, [NotBody] IMessageBus bus,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService)
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
        if (invite is null) return Results.NotFound();

        if(invite.State == InviteState.Expired) return Results.BadRequest("Invite has expired");
        if(invite.IsExpired(DateTimeOffset.UtcNow)) return Results.BadRequest("Invite has expired");
        if(invite.IsExhausted()) return Results.BadRequest("Invite has reached its maximum number of uses");

        var isBanned = await ctx.Set<GuildBan>().AnyAsync(b => b.GuildId == invite.GuildId && b.BannedUserId == userId);
        if (isBanned) return Results.Forbid();

        // Redeeming while already a member is a no-op, not a second membership. Without this an
        // account that redeemed two invites for the same guild ended up with two GuildMember rows,
        // and every m.UserId == userId lookup in the service picks an arbitrary one of them.
        // Deliberately checked before UseCount++ so a re-redeem doesn't burn a use either.
        var alreadyMember = await ctx.GuildMembers
            .AnyAsync(m => m.GuildId == invite.GuildId && m.UserId == userId);
        if (alreadyMember) return Results.Conflict("User is already a member of this guild.");

        invite.UseCount++;
        if(invite.Type == InviteType.OneTime || invite.IsExhausted()) invite.State = InviteState.Expired;

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
            Nickname = profileResponse.Profile.UserName,
            SearchValue = searchValue.ToUpperInvariant(),
            // Only members who join while onboarding is actually configured are gated by it -
            // everyone else is auto-completed at join time, same as today's ungated join.
            OnboardingCompletedAt = onboardingRequired ? null : joinedAt,
        };

        ctx.GuildMembers.Add(member);

        // A guild with no @everyone role is malformed rather than impossible - imports and
        // template instantiation both build the role set themselves - and the previous `role!`
        // turned that into an NRE (a 500) after the member had already been added to the change
        // tracker. Fail the join cleanly instead; nothing is committed on this path.
        var role = guild.Roles.FirstOrDefault(r => r.Type == RoleType.Everyone);
        if (role is null)
            return Results.Problem("Guild is missing its @everyone role; cannot complete the join.",
                statusCode: StatusCodes.Status500InternalServerError);

        role.Members.Add(new RoleMember()
        {
            Id = RoleMember.GenerateId(),
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            MemberId = member.Id,
            RoleId = role.Id,
        });

        var cacheKeyOne = GuildPermissionsForUser.GetCacheKey(guild.Id, userId);
        await cache.RemoveAsync(cacheKeyOne);

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

        // Discord-style system message in the guild's system channel. Content carries a plain-
        // English fallback for consumers that don't understand MessageType (bots, notifications);
        // real clients render one of ~10 localized copy variants keyed by (Type, SystemMessageVariant)
        // instead, same convention as Discord's own system messages.
        if (!string.IsNullOrWhiteSpace(guild.SystemChannelId))
        {
            await bus.InvokeAsync(new CreateMessageCommand()
            {
                Content = Encoding.UTF8.GetBytes($"{profileResponse.Profile.UserName} joined the server"),
                ChannelId = guild.SystemChannelId,
                AuthorId = userId,
                AuthorIdType = AuthorIdType.User,
                Mentions = [],
                Type = MessagingMessageType.GuildMemberJoin,
            });
        }

        return Results.Accepted();

    }
}