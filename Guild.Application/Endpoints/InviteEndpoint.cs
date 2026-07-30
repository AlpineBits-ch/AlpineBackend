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

    public async Task<IResult> CreateInviteAsync(string guildId, CreateInviteDto createInviteDto, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
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
        return Results.Ok(invite.ToFacet<GuildInvite, InviteDto>());
    }

    [WolverineGet("/api/v1/invites/code/{code}")]
    public async Task<IResult> GetInviteByCodeAsync(string code, [NotBody] MicroserviceContext ctx)
    {
        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Code == code);
        if(invite is null) return Results.NotFound();
        return Results.Ok(invite.ToFacet<GuildInvite, InviteDto>());
    }
    [WolverineDelete("/api/v1/invites/{inviteId}")]
    public async Task<IResult> DeleteInviteAsync(string inviteId, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        var invite = await ctx.GuildInvites.Include(g => g.Guild).FirstOrDefaultAsync(i => i.Id == inviteId);

        if(invite is null) return Results.NotFound();
        
        if(!await permissionService.CanUserPerformActionOnGuildAsync(userId, invite.GuildId, Permissions.ManageChannel))
            return Results.Forbid();
        
        ctx.GuildInvites.Remove(invite);
        
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
            Nickname = profileResponse.Profile.UserName,
            SearchValue = searchValue.ToUpperInvariant(),
            // Only members who join while onboarding is actually configured are gated by it -
            // everyone else is auto-completed at join time, same as today's ungated join.
            OnboardingCompletedAt = onboardingRequired ? null : joinedAt,
        };

        ctx.GuildMembers.Add(member);

        var role = guild.Roles.FirstOrDefault(r => r.Type == RoleType.Everyone);
        
        role!.Members.Add(new RoleMember()
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