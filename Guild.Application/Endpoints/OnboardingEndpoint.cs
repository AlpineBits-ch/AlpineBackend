using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class OnboardingEndpoint
{
    [WolverineGet("/api/v1/guilds/{guildId}/onboarding")]
    public async Task<IResult> GetConfig(string guildId, [NotBody] GuildPermissionService permissionService,
        [NotBody] OnboardingConfigService configService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        return Results.Ok(await configService.GetAsync(guildId));
    }

    /// <summary>Whole-document replace, matching Discord's PUT /guilds/{id}/onboarding: prompts
    /// carrying a known id are updated in place, prompts without one are created, and anything
    /// stored but absent from the payload is deleted.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/onboarding")]
    public async Task<IResult> UpdateConfig(string guildId, UpdateOnboardingConfigDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] OnboardingConfigService configService,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var (error, config) = await configService.ReplaceAsync(guildId, userId, dto);
        return error is not null ? Results.BadRequest(error) : Results.Ok(config);
    }

    /// <summary>What a member sees before accepting - rules text, the prompts to answer, default
    /// channels, and whether they've already accepted.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/onboarding/me")]
    public async Task<IResult> GetMyStatus(string guildId, [NotBody] MicroserviceContext ctx,
        [NotBody] OnboardingConfigService configService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        var config = await ctx.Set<GuildOnboardingConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);
        var prompts = await configService.LoadPromptsAsync(guildId, inOnboardingOnly: true);

        return Results.Ok(new
        {
            // Clients gate the whole onboarding screen on this: a member can be un-accepted while
            // the guild's onboarding is switched off, in which case there is nothing to show and
            // nothing restricting them.
            enabled = config?.Enabled ?? false,
            completed = member.OnboardingCompletedAt is not null,
            rulesText = config?.RulesText,
            defaultChannelIds = config?.DefaultChannelIds ?? [],
            prompts = prompts.Select(OnboardingConfigService.ToDto).ToList(),
        });
    }

    [WolverinePost("/api/v1/guilds/{guildId}/onboarding/accept")]
    public async Task<IResult> Accept(string guildId, OnboardingResponsesDto dto, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService,
        [NotBody] OnboardingGrantService grantService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        // Idempotent: a second accept is a no-op rather than an error, and deliberately does not
        // re-apply answers - changing picks after the fact goes through Channels & Roles instead.
        if (member.OnboardingCompletedAt is not null) return Results.Ok();

        var error = await grantService.ValidateResponsesAsync(guildId, dto.Responses);
        if (error is not null) return Results.BadRequest(error);

        var selectedOptionIds = dto.Responses.SelectMany(r => r.OptionIds).Distinct().ToList();
        await grantService.ApplySelectionAsync(guildId, member, selectedOptionIds);

        member.OnboardingCompletedAt = DateTimeOffset.UtcNow;
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, userId);

        // Discord's GUILD_MEMBER_UPDATE with pending: false - bots watching the join flow need to
        // know the member became able to participate, and may have gained roles.
        await bus.PublishAsync(new MemberUpdatedForBots { GuildId = guildId, UserId = userId });

        return Results.Ok();
    }

    /// <summary>The post-join "Channels &amp; Roles" screen: every prompt, including the ones flagged
    /// out of the join flow, with this member's current picks marked.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/onboarding/prompts")]
    public async Task<IResult> GetMyPrompts(string guildId, [NotBody] MicroserviceContext ctx,
        [NotBody] OnboardingConfigService configService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        var prompts = await configService.LoadPromptsAsync(guildId);

        var selectedOptionIds = await ctx.Set<GuildMemberOnboardingResponse>()
            .AsNoTracking()
            .Where(r => r.MemberId == member.Id)
            .Select(r => r.OptionId)
            .ToHashSetAsync();

        return Results.Ok(prompts.Select(p => new MemberOnboardingPromptDto
        {
            Id = p.Id,
            Title = p.Title,
            Type = p.Type,
            SingleSelect = p.SingleSelect,
            Required = p.Required,
            InOnboarding = p.InOnboarding,
            Position = p.Position,
            Options = p.Options.OrderBy(o => o.Position).Select(o => new MemberOnboardingPromptOptionDto
            {
                Id = o.Id,
                Title = o.Title,
                Description = o.Description,
                Emoji = o.Emoji,
                RoleIds = o.RoleIds,
                ChannelIds = o.ChannelIds,
                Position = o.Position,
                Selected = selectedOptionIds.Contains(o.Id),
            }).ToList(),
        }).ToList());
    }

    /// <summary>Replaces the member's picks wholesale. Newly selected options grant their roles and
    /// channels; deselected ones give back exactly what onboarding handed out for them and nothing
    /// else - see OnboardingGrantService.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/onboarding/me/responses")]
    public async Task<IResult> UpdateMyResponses(string guildId, OnboardingResponsesDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] OnboardingGrantService grantService,
        [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        var error = await grantService.ValidateResponsesAsync(guildId, dto.Responses);
        if (error is not null) return Results.BadRequest(error);

        var selectedOptionIds = dto.Responses.SelectMany(r => r.OptionIds).Distinct().ToList();
        await grantService.ApplySelectionAsync(guildId, member, selectedOptionIds);

        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, userId);
        await bus.PublishAsync(new MemberUpdatedForBots { GuildId = guildId, UserId = userId });

        return Results.Ok();
    }

}
