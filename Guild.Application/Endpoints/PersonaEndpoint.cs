using System.Security.Claims;
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
/// Characters, and who may speak as them. A persona is a costume rather than a subject of
/// authorization, so it never acquires a GuildMember row and AuthorId keeps naming the real
/// account on everything it posts.
/// </summary>
[Authorize]
public class PersonaEndpoint
{
    /// <summary>The caller's own personas, across every guild.</summary>
    [WolverineGet("/api/v1/personas")]
    public async Task<IResult> ListOwnAsync(
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var personas = await ctx.Set<Persona>()
            .AsNoTracking()
            .Where(p => p.Scope == PersonaScope.User && p.OwnerUserId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return Results.Ok(personas.Select(PersonaDto.From).ToList());
    }

    /// <summary>
    /// Creates a global persona. There is no guild permission for this: a user in zero roleplay
    /// guilds holds no guild mask at all, so the capability lives on the account surface (spec 8).
    /// </summary>
    [WolverinePost("/api/v1/personas")]
    public async Task<IResult> CreateOwnAsync(CreatePersonaDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (PersonaDisplayGuard.ValidateCore(dto.Name, dto.AvatarUrl, dto.Pronouns, dto.Color, dto.ShortBio) is { } error)
            return Results.BadRequest(error);

        var count = await ctx.Set<Persona>()
            .CountAsync(p => p.Scope == PersonaScope.User && p.OwnerUserId == userId);
        if (count >= PersonaLimits.MaxPersonasPerUser)
            return Results.BadRequest($"You cannot have more than {PersonaLimits.MaxPersonasPerUser} personas.");

        var persona = Persona.Create(new CreatePersonaParams
        {
            Scope = PersonaScope.User,
            OwnerUserId = userId,
            Name = dto.Name.Trim(),
            AvatarUrl = dto.AvatarUrl?.Trim(),
            Pronouns = dto.Pronouns?.Trim(),
            Color = dto.Color?.Trim(),
            ShortBio = dto.ShortBio?.Trim(),
        });

        ctx.Set<Persona>().Add(persona);

        return Results.Ok(PersonaDto.From(persona));
    }

    /// <summary>One of the caller's own personas.</summary>
    [WolverineGet("/api/v1/personas/{personaId}")]
    public async Task<IResult> GetOwnAsync(string personaId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var persona = await ctx.Set<Persona>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null || persona.Scope != PersonaScope.User || persona.OwnerUserId != userId)
            return Results.NotFound();

        return Results.Ok(PersonaDto.From(persona));
    }

    /// <summary>Edits a global persona.</summary>
    [WolverinePatch("/api/v1/personas/{personaId}")]
    public async Task<IResult> UpdateOwnAsync(string personaId, UpdatePersonaDto dto,
        [NotBody] PersonaService personas, [NotBody] PersonaDisplayGuard displayGuard,
        [NotBody] HouseholdChannelService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var persona = await ctx.Set<Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null || persona.Scope != PersonaScope.User || persona.OwnerUserId != userId)
            return Results.NotFound();

        var result = await ApplyAsync(persona, dto, displayGuard);
        if (result is not null) return result;

        await personas.InvalidatePersonaAsync(persona.Id);
        await BroadcastPersonaAsync(realtime, ctx, persona);

        return Results.Ok(PersonaDto.From(persona));
    }

    /// <summary>
    /// Deletes a persona that has never spoken, and retires one that has - a retired persona keeps
    /// rendering on historic messages so Message.PersonaId never dangles.
    /// </summary>
    [WolverineDelete("/api/v1/personas/{personaId}")]
    public async Task<IResult> DeleteOwnAsync(string personaId,
        [NotBody] PersonaService personas, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var persona = await ctx.Set<Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null || persona.Scope != PersonaScope.User || persona.OwnerUserId != userId)
            return Results.NotFound();

        await personas.InvalidatePersonaAsync(persona.Id);

        return Results.Ok(RemoveOrRetire(ctx, persona));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Guild personas and grants

    /// <summary>
    /// Everything the caller may speak as in this guild: their own adopted personas plus the
    /// guild-scoped ones they hold a grant on.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/personas")]
    public async Task<IResult> ListForGuildAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaPageService pages, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.UsePersonas) is { } denied)
            return denied;

        var usable = await personas.GetUsablePersonasAsync(userId, guildId);
        if (usable.Count == 0) return Results.Ok(new List<PersonaGuildProfileDto>());

        var requiresApproval = await personas.RequiresApprovalAsync(guildId);
        var ids = usable.Select(u => u.PersonaId).ToList();

        var rows = await ctx.Set<Persona>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();
        var profiles = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId && ids.Contains(p.PersonaId))
            .ToListAsync();

        var dtos = new List<PersonaGuildProfileDto>(profiles.Count);
        foreach (var profile in profiles)
        {
            var persona = rows.First(p => p.Id == profile.PersonaId);
            var canSpeak = await personas.DenyReasonAsync(
                userId, guildId, usable.First(u => u.PersonaId == profile.PersonaId), requiresApproval) is null;

            dtos.Add(await ToProfileDtoAsync(pages, persona, profile, canSpeak));
        }

        return Results.Ok(dtos.OrderBy(d => d.Persona?.Name).ToList());
    }

    /// <summary>
    /// Every character this guild has adopted, as everybody sees them. The list above answers "what
    /// may I speak as", which is the composer's question; drawing a scene's turn order, a cast
    /// picker or a mention token is about other players' characters, and this is what names them.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/personas/cast")]
    public async Task<IResult> ListCastAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaCastService cast,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        bool includeRetired = false)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // Membership only: a character's name and avatar are already on every message it has sent,
        // and nothing here says who plays it.
        if (await PersonaGate.CheckMembershipAsync(permissionService, ctx, guildId, userId) is { } denied)
            return denied;

        return Results.Ok(await cast.ListAsync(guildId, includeRetired));
    }

    /// <summary>Creates a persona owned by the guild - a Narrator, a town guard, a recurring
    /// antagonist - which a grant then lets a role or a person speak as.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas")]
    public async Task<IResult> CreateForGuildAsync(string guildId, CreatePersonaDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaDisplayGuard displayGuard, [NotBody] AuditLogService auditLog,
        [NotBody] PersonaPageService pages, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ManageAnyPersona) is { } denied)
            return denied;

        if (PersonaDisplayGuard.ValidateCore(dto.Name, dto.AvatarUrl, dto.Pronouns, dto.Color, dto.ShortBio) is { } error)
            return Results.BadRequest(error);

        var count = await ctx.Set<Persona>()
            .CountAsync(p => p.Scope == PersonaScope.Guild && p.OwnerGuildId == guildId);
        if (count >= PersonaLimits.MaxPersonasPerGuild)
            return Results.BadRequest($"A guild cannot have more than {PersonaLimits.MaxPersonasPerGuild} personas.");

        if (await displayGuard.FindNameCollisionAsync(guildId, dto.Name) is { } collision)
            return Results.Conflict($"That name is already {collision}.");

        var persona = Persona.Create(new CreatePersonaParams
        {
            Scope = PersonaScope.Guild,
            OwnerGuildId = guildId,
            Name = dto.Name.Trim(),
            AvatarUrl = dto.AvatarUrl?.Trim(),
            Pronouns = dto.Pronouns?.Trim(),
            Color = dto.Color?.Trim(),
            ShortBio = dto.ShortBio?.Trim(),
        });

        // A guild persona is adopted into its own guild at creation, because the profile is where
        // its proxy prefix and approval state live and it is unusable without one.
        var profile = PersonaGuildProfile.Create(new CreatePersonaGuildProfileParams
        {
            PersonaId = persona.Id,
            GuildId = guildId,
        });

        ctx.Set<Persona>().Add(persona);
        ctx.Set<PersonaGuildProfile>().Add(profile);

        auditLog.Log(guildId, userId, AuditActionType.PersonaCreated, persona.Id, new { persona.Name });
        await personas.InvalidateGuildAsync(guildId);

        return Results.Ok(await ToProfileDtoAsync(pages, persona, profile, canSpeak: false));
    }

    /// <summary>Edits a guild-scoped persona.</summary>
    [WolverinePatch("/api/v1/guilds/{guildId}/personas/{personaId}")]
    public async Task<IResult> UpdateForGuildAsync(string guildId, string personaId, UpdatePersonaDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaDisplayGuard displayGuard, [NotBody] AuditLogService auditLog,
        [NotBody] HouseholdChannelService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ManageAnyPersona) is { } denied)
            return denied;

        var persona = await ctx.Set<Persona>()
            .FirstOrDefaultAsync(p => p.Id == personaId && p.Scope == PersonaScope.Guild && p.OwnerGuildId == guildId);
        if (persona is null) return Results.NotFound();

        var result = await ApplyAsync(persona, dto, displayGuard, guildId);
        if (result is not null) return result;

        auditLog.Log(guildId, userId, AuditActionType.PersonaUpdated, persona.Id, new { persona.Name });
        await personas.InvalidateGuildAsync(guildId);
        await BroadcastPersonaAsync(realtime, ctx, persona);

        return Results.Ok(PersonaDto.From(persona));
    }

    /// <summary>Deletes, or retires, a guild-scoped persona.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/personas/{personaId}")]
    public async Task<IResult> DeleteForGuildAsync(string guildId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] AuditLogService auditLog, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ManageAnyPersona) is { } denied)
            return denied;

        var persona = await ctx.Set<Persona>()
            .FirstOrDefaultAsync(p => p.Id == personaId && p.Scope == PersonaScope.Guild && p.OwnerGuildId == guildId);
        if (persona is null) return Results.NotFound();

        var outcome = RemoveOrRetire(ctx, persona);

        auditLog.Log(guildId, userId, AuditActionType.PersonaDeleted, persona.Id,
            new { persona.Name, outcome.Retired });
        await personas.InvalidateGuildAsync(guildId);

        return Results.Ok(outcome);
    }

    /// <summary>Who may currently speak as a guild-scoped persona.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/personas/{personaId}/grants")]
    public async Task<IResult> ListGrantsAsync(string guildId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ManageAnyPersona) is { } denied)
            return denied;

        if (!await OwnedByGuildAsync(ctx, guildId, personaId)) return Results.NotFound();

        var grants = await ctx.Set<PersonaGrant>()
            .AsNoTracking()
            .Where(g => g.PersonaId == personaId)
            .ToListAsync();

        return Results.Ok(grants.Select(PersonaGrantDto.From).ToList());
    }

    /// <summary>Grants a role or a user the right to speak as a guild-scoped persona.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/grants")]
    public async Task<IResult> CreateGrantAsync(string guildId, string personaId, CreatePersonaGrantDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] AuditLogService auditLog, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ManageAnyPersona) is { } denied)
            return denied;

        if (!await OwnedByGuildAsync(ctx, guildId, personaId)) return Results.NotFound();

        var hasRole = !string.IsNullOrWhiteSpace(dto.RoleId);
        var hasUser = !string.IsNullOrWhiteSpace(dto.UserId);
        if (hasRole == hasUser) return Results.BadRequest("Send exactly one of roleId or userId.");

        if (hasRole && !await ctx.Roles.AnyAsync(r => r.Id == dto.RoleId && r.GuildId == guildId))
            return Results.BadRequest("That role is not in this guild.");

        if (hasUser && !await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == dto.UserId))
            return Results.BadRequest("That user is not a member of this guild.");

        var already = await ctx.Set<PersonaGrant>().AnyAsync(g =>
            g.PersonaId == personaId &&
            ((hasRole && g.RoleId == dto.RoleId) || (hasUser && g.UserId == dto.UserId)));
        if (already) return Results.Conflict("That grant already exists.");

        // Widening who can reach a persona can introduce a prefix collision without any prefix
        // being edited, so the same check runs here as on the profile.
        var reaches = await ResolveGranteeUserIdsAsync(ctx, guildId, dto);
        if (await personas.FindGrantProxyCollisionAsync(guildId, personaId, reaches) is { } collision)
            return Results.Conflict($"That grant would make this persona's proxy prefix ambiguous with {collision.Name}.");

        var grant = PersonaGrant.Create(new CreatePersonaGrantParams
        {
            PersonaId = personaId,
            RoleId = hasRole ? dto.RoleId : null,
            UserId = hasUser ? dto.UserId : null,
        });

        ctx.Set<PersonaGrant>().Add(grant);

        auditLog.Log(guildId, userId, AuditActionType.PersonaGrantCreated, personaId,
            new { grant.RoleId, grant.UserId });
        await personas.InvalidateGuildAsync(guildId);

        return Results.Ok(PersonaGrantDto.From(grant));
    }

    /// <summary>Revokes a grant, which stops that speech at the next message rather than the next
    /// cache expiry.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/personas/{personaId}/grants/{grantId}")]
    public async Task<IResult> DeleteGrantAsync(string guildId, string personaId, string grantId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] AuditLogService auditLog, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ManageAnyPersona) is { } denied)
            return denied;

        if (!await OwnedByGuildAsync(ctx, guildId, personaId)) return Results.NotFound();

        var grant = await ctx.Set<PersonaGrant>()
            .FirstOrDefaultAsync(g => g.Id == grantId && g.PersonaId == personaId);
        if (grant is null) return Results.NotFound();

        ctx.Set<PersonaGrant>().Remove(grant);

        auditLog.Log(guildId, userId, AuditActionType.PersonaGrantDeleted, personaId,
            new { grant.RoleId, grant.UserId });
        await personas.InvalidateGuildAsync(guildId);

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    internal static async Task<PersonaGuildProfileDto> ToProfileDtoAsync(
        PersonaPageService pages, Persona persona, PersonaGuildProfile profile, bool canSpeak) => new()
    {
        PersonaId = profile.PersonaId,
        GuildId = profile.GuildId,
        DisplayName = profile.DisplayName,
        AvatarUrl = profile.AvatarUrl,
        Tag = profile.Tag,
        ProxyPrefix = profile.ProxyPrefix,
        ProxySuffix = profile.ProxySuffix,
        WikiPageId = profile.WikiPageId,
        UpstreamRevisionNumber = profile.UpstreamRevisionNumber,
        UpstreamState = await pages.ResolveUpstreamStateAsync(persona, profile),
        ApprovalState = profile.ApprovalState,
        ApprovedByUserId = profile.ApprovedByUserId,
        ApprovedAt = profile.ApprovedAt,
        ChangesRequestedReason = profile.ChangesRequestedReason,
        CanSpeak = canSpeak,
        Persona = PersonaDto.From(persona),
    };

    /// <summary>Applies a patch, validating the display fields and the guild collisions they can
    /// cause. Returns the failing result, or null.</summary>
    private static async Task<IResult?> ApplyAsync(
        Persona persona, UpdatePersonaDto dto, PersonaDisplayGuard displayGuard, string? guildId = null)
    {
        var name = dto.Name?.Trim() ?? persona.Name;

        if (PersonaDisplayGuard.ValidateCore(
                name,
                dto.AvatarUrl.Or(persona.AvatarUrl)?.Trim(),
                dto.Pronouns.Or(persona.Pronouns),
                dto.Color.Or(persona.Color),
                dto.ShortBio.Or(persona.ShortBio)) is { } error)
            return Results.BadRequest(error);

        if (!string.Equals(name, persona.Name, StringComparison.Ordinal))
        {
            var collision = guildId is null
                ? await displayGuard.FindNameCollisionAcrossProfilesAsync(persona.Id, name)
                : await displayGuard.FindNameCollisionAsync(guildId, name);

            if (collision is not null) return Results.Conflict($"That name is already {collision}.");
        }

        persona.Name = name;
        if (dto.AvatarUrl.HasValue) persona.AvatarUrl = Blank(dto.AvatarUrl.Value);
        if (dto.Pronouns.HasValue) persona.Pronouns = Blank(dto.Pronouns.Value);
        if (dto.Color.HasValue) persona.Color = Blank(dto.Color.Value);
        if (dto.ShortBio.HasValue) persona.ShortBio = Blank(dto.ShortBio.Value);
        persona.UpdatedAt = DateTimeOffset.UtcNow;

        // Retiring goes through the entity; un-retiring has no transition of its own because it
        // takes nothing away.
        if (dto.IsRetired == true) persona.Retire();
        else if (dto.IsRetired == false) persona.IsRetired = false;

        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A persona that has spoken retires rather than deletes, so no message is left pointing at a
    /// row that is gone.
    /// </summary>
    private static PersonaDeletionDto RemoveOrRetire(MicroserviceContext ctx, Persona persona)
    {
        if (persona.HasSpoken)
        {
            persona.Retire();

            return new PersonaDeletionDto
            {
                PersonaId = persona.Id,
                Retired = true,
                Reason = "This persona has posted messages, so it was retired instead of deleted.",
            };
        }

        ctx.Set<Persona>().Remove(persona);
        return new PersonaDeletionDto { PersonaId = persona.Id, Retired = false };
    }

    private static async Task<bool> OwnedByGuildAsync(MicroserviceContext ctx, string guildId, string personaId) =>
        await ctx.Set<Persona>()
            .AnyAsync(p => p.Id == personaId && p.Scope == PersonaScope.Guild && p.OwnerGuildId == guildId);

    /// <summary>Every user a proposed grant would newly reach.</summary>
    private static async Task<List<string>> ResolveGranteeUserIdsAsync(
        MicroserviceContext ctx, string guildId, CreatePersonaGrantDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.UserId)) return [dto.UserId];

        var now = DateTimeOffset.UtcNow;
        return await ctx.RoleMembers
            .AsNoTracking()
            .Where(rm => rm.RoleId == dto.RoleId && (rm.ExpiresAt == null || rm.ExpiresAt > now))
            .Join(ctx.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId),
                rm => rm.MemberId, m => m.Id, (_, m) => m.UserId)
            .Distinct()
            .ToListAsync();
    }

    /// <summary>
    /// guild.PersonaUpdated, on the guild hub, for every guild the persona is adopted into - the
    /// name and avatar it renders under changed everywhere at once.
    /// </summary>
    private static async Task BroadcastPersonaAsync(
        HouseholdChannelService realtime, MicroserviceContext ctx, Persona persona)
    {
        var guildIds = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.PersonaId == persona.Id)
            .Select(p => p.GuildId)
            .Distinct()
            .ToListAsync();

        foreach (var guildId in guildIds)
        {
            await realtime.BroadcastGuildAsync(guildId, "guild.PersonaUpdated", new
            {
                GuildId = guildId,
                PersonaId = persona.Id,
                persona.Name,
                persona.AvatarUrl,
            });
        }
    }
}
