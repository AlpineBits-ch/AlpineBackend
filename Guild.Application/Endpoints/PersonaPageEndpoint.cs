using System.Security.Claims;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// A character page is an ordinary wiki page, served by the wiki endpoints. Only two operations
/// are specific to personas: making this guild's copy, and pulling from the reference copy again.
/// </summary>
[Authorize]
public class PersonaPageEndpoint
{
    /// <summary>
    /// Creates this guild's character page, copying the reference copy's content, infobox, icon
    /// and cover when one exists, and links it to the profile.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/page")]
    public async Task<IResult> CreateAsync(string guildId, string personaId, CreatePersonaPageDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaPageService pages,
        [NotBody] PersonaService personas, [NotBody] RoleplayRealtimeService realtime,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var (persona, profile, denied) = await ResolveAsync(permissionService, ctx, guildId, personaId, userId);
        if (denied is not null) return denied;

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Wiki))
            return Results.Forbid();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.CreateWikiPages))
            return Results.Forbid();

        if (profile!.WikiPageId is not null)
            return Results.Conflict("This character already has a page in this guild.");

        if (!string.IsNullOrWhiteSpace(dto.CategoryId) &&
            !await ctx.WikiCategories.AnyAsync(c => c.Id == dto.CategoryId && c.GuildId == guildId))
            return Results.BadRequest("That wiki category is not in this guild.");

        var title = string.IsNullOrWhiteSpace(dto.Title)
            ? PersonaDisplayGuard.EffectiveName(persona!, profile)
            : dto.Title.Trim();

        var page = await pages.CreatePageAsync(persona!, profile, title, Blank(dto.CategoryId), userId);

        await personas.InvalidateGuildAsync(guildId);
        await realtime.PageCreatedAsync(guildId, personaId, page);

        return Results.Ok(page.ToFacet<WikiPage, WikiPageDto>());
    }

    /// <summary>
    /// Merges the reference copy into this guild's copy. A <c>preview</c> returns the diff and
    /// writes nothing; every strategy but <c>TakeUpstream</c> keeps local edits where the two sides
    /// disagree, because a pull that fast-forwards silently discards a moderator's work.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/page/pull")]
    public async Task<IResult> PullAsync(string guildId, string personaId, PullPersonaPageDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaPageService pages,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var (persona, profile, denied) = await ResolveAsync(permissionService, ctx, guildId, personaId, userId);
        if (denied is not null) return denied;

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Wiki))
            return Results.Forbid();

        if (profile!.WikiPageId is null)
            return Results.NotFound();

        // A preview reads; anything that writes needs the permission to edit somebody else's page,
        // because the local copy is guild-owned rather than the character owner's.
        if (dto.Strategy != PersonaPagePullStrategy.Preview &&
            !await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.EditAnyWikiPage))
            return Results.Forbid();

        if (dto.Strategy == PersonaPagePullStrategy.Preview &&
            !await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki))
            return Results.Forbid();

        var pull = await pages.PullAsync(persona!, profile, dto.Strategy, userId);

        await realtime.PagePulledAsync(guildId, dto.Strategy, pull);

        return Results.Ok(pull);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The persona and its profile in this guild, plus the failing result when the caller may not
    /// touch either.
    /// </summary>
    private static async Task<(Persona?, PersonaGuildProfile?, IResult?)> ResolveAsync(
        GuildPermissionService permissionService, MicroserviceContext ctx,
        string guildId, string personaId, string userId)
    {
        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.UsePersonas) is { } denied)
            return (null, null, denied);

        var persona = await ctx.Set<Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return (null, null, Results.NotFound());

        var profile = await ctx.Set<PersonaGuildProfile>()
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PersonaId == personaId);
        if (profile is null) return (null, null, Results.NotFound());

        var isOwner = persona.Scope == PersonaScope.User && persona.OwnerUserId == userId;
        if (!isOwner &&
            !await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageAnyPersona))
            return (null, null, Results.Forbid());

        return (persona, profile, null);
    }
}
