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

namespace Guild.Application.Endpoints.Persona;

/// <summary>
/// Adopting a character into a guild, its per-guild overrides, the approval queue, and the
/// channel's autoproxy state.
/// </summary>
[Authorize]
public class PersonaProfileEndpoint
{
    /// <summary>
    /// Adopts a persona into this guild and sets its per-guild overrides. A PUT, so an omitted
    /// override clears whatever was there.
    /// </summary>
    [WolverinePut("/api/v1/guilds/{guildId}/personas/{personaId}/profile")]
    public async Task<IResult> UpsertAsync(string guildId, string personaId, UpsertPersonaProfileDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaDisplayGuard displayGuard, [NotBody] PersonaPageService pages,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckMembershipAsync(permissionService, ctx, guildId, userId) is { } blocked)
            return blocked;

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return Results.NotFound();

        if (await AuthorizeWriteAsync(permissionService, guildId, userId, persona) is { } denied)
            return denied;

        if (PersonaDisplayGuard.ValidateProfile(
                dto.DisplayName, dto.AvatarUrl, dto.Tag, dto.ProxyPrefix, dto.ProxySuffix) is { } error)
            return Results.BadRequest(error);

        var displayName = Blank(dto.DisplayName);
        if (displayName is not null && await displayGuard.FindNameCollisionAsync(guildId, displayName) is { } collision)
            return Results.Conflict($"That display name is already {collision}.");

        var prefix = Blank(dto.ProxyPrefix);
        var suffix = Blank(dto.ProxySuffix);

        if (await personas.FindProxyCollisionAsync(guildId, persona, prefix, suffix) is { } clash)
            return Results.Conflict($"That proxy prefix already selects {clash.Name} here.");

        var profile = await ctx.Set<PersonaGuildProfile>()
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PersonaId == personaId);

        var isAdoption = profile is null;

        if (profile is null)
        {
            var adopted = await ctx.Set<PersonaGuildProfile>()
                .Join(ctx.Set<Domain.Entity.Persona>().Where(p => p.OwnerUserId == userId),
                    p => p.PersonaId, p => p.Id, (p, _) => p.GuildId)
                .CountAsync(g => g == guildId);

            if (adopted >= PersonaLimits.MaxProfilesPerUserPerGuild)
                return Results.BadRequest(
                    $"You cannot adopt more than {PersonaLimits.MaxProfilesPerUserPerGuild} personas into one guild.");

            profile = PersonaGuildProfile.Create(new CreatePersonaGuildProfileParams
            {
                PersonaId = personaId,
                GuildId = guildId,
            });

            ctx.Set<PersonaGuildProfile>().Add(profile);
        }

        profile.DisplayName = displayName;
        profile.AvatarUrl = Blank(dto.AvatarUrl);
        profile.Tag = Blank(dto.Tag);
        profile.ProxyPrefix = prefix;
        profile.ProxySuffix = suffix;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await personas.InvalidateGuildAsync(guildId);

        var canSpeak = await CanSpeakAsync(personas, userId, guildId, personaId);

        if (isAdoption) await realtime.PersonaAdoptedAsync(persona, profile, canSpeak);
        await realtime.ProfileChangedAsync(profile, canSpeak);

        return Results.Ok(await PersonaEndpoint.ToProfileDtoAsync(pages, persona, profile, canSpeak));
    }

    /// <summary>
    /// Un-adopts a persona from this guild. Historic messages keep the name and avatar they were
    /// sent under, as with any other author display data.
    /// </summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/personas/{personaId}/profile")]
    public async Task<IResult> DeleteAsync(string guildId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckMembershipAsync(permissionService, ctx, guildId, userId) is { } blocked)
            return blocked;

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return Results.NotFound();

        var isOwner = persona.Scope == PersonaScope.User && persona.OwnerUserId == userId;
        if (!isOwner &&
            !await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageAnyPersona))
            return Results.Forbid();

        var profile = await ctx.Set<PersonaGuildProfile>()
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PersonaId == personaId);
        if (profile is null) return Results.NotFound();

        await personas.RepointHomeAsync(persona, profile.Id);

        var autoproxy = await ctx.Set<PersonaAutoproxyState>()
            .Where(a => a.GuildId == guildId && a.PersonaId == personaId)
            .ToListAsync();
        ctx.Set<PersonaAutoproxyState>().RemoveRange(autoproxy);

        ctx.Set<PersonaGuildProfile>().Remove(profile);

        await personas.InvalidateGuildAsync(guildId);
        await realtime.PersonaUnadoptedAsync(guildId, personaId);

        return Results.NoContent();
    }

    /// <summary>Puts a character in front of the approval queue.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/profile/submit")]
    public async Task<IResult> SubmitAsync(string guildId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaPageService pages, [NotBody] RoleplayRealtimeService realtime,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckMembershipAsync(permissionService, ctx, guildId, userId) is { } blocked)
            return blocked;

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return Results.NotFound();

        if (await AuthorizeWriteAsync(permissionService, guildId, userId, persona) is { } denied)
            return denied;

        var profile = await ctx.Set<PersonaGuildProfile>()
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PersonaId == personaId);
        if (profile is null) return Results.NotFound();

        var isResubmission = profile.ApprovalState == PersonaApprovalState.ChangesRequested;

        // An approved character stays approved through an edit - the pending revisions are what the
        // reviewer sees, and blocking speech on a typo fix is how approval queues become resented.
        try
        {
            profile.Submit();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }

        await personas.InvalidateGuildAsync(guildId);
        await realtime.ProfileChangedAsync(profile, canSpeak: false);
        await realtime.ReviewRequestedAsync(persona, profile, isResubmission);

        return Results.Ok(await PersonaEndpoint.ToProfileDtoAsync(pages, persona, profile, canSpeak: false));
    }

    /// <summary>Approves a character, which is what lets it speak in a guild that requires it.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/profile/approve")]
    public async Task<IResult> ApproveAsync(string guildId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaPageService pages, [NotBody] AuditLogService auditLog,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ApprovePersonas) is { } denied)
            return denied;

        var (persona, profile) = await LoadAsync(ctx, guildId, personaId);
        if (persona is null || profile is null) return Results.NotFound();

        // Everything above this number is a pending edit the next reviewer sees as a diff.
        var revisionNumber = profile.WikiPageId is null
            ? (int?)null
            : await pages.LatestRevisionNumberAsync(profile.WikiPageId);

        try
        {
            profile.Approve(userId, revisionNumber);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }

        auditLog.Log(guildId, userId, AuditActionType.PersonaApproved, personaId, new { persona.Name });

        await personas.InvalidateGuildAsync(guildId);
        await realtime.ProfileChangedAsync(profile, canSpeak: !persona.IsRetired);
        await realtime.ReviewCompletedAsync(persona, profile, userId, canSpeak: !persona.IsRetired);

        return Results.Ok(await PersonaEndpoint.ToProfileDtoAsync(pages, persona, profile, canSpeak: !persona.IsRetired));
    }

    /// <summary>Sends a character back with a reason.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/profile/request-changes")]
    public async Task<IResult> RequestChangesAsync(string guildId, string personaId, RequestPersonaChangesDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaPageService pages, [NotBody] AuditLogService auditLog,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ApprovePersonas) is { } denied)
            return denied;

        var reason = dto.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) return Results.BadRequest("A reason is required.");
        if (reason.Length > MaxReasonLength)
            return Results.BadRequest($"The reason cannot exceed {MaxReasonLength} characters.");

        var (persona, profile) = await LoadAsync(ctx, guildId, personaId);
        if (persona is null || profile is null) return Results.NotFound();

        try
        {
            profile.RequestChanges(reason);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }

        auditLog.Log(guildId, userId, AuditActionType.PersonaRejected, personaId, new { persona.Name, Reason = reason });

        await personas.InvalidateGuildAsync(guildId);
        await realtime.ProfileChangedAsync(profile, canSpeak: false);
        await realtime.ReviewCompletedAsync(persona, profile, userId, canSpeak: false);

        return Results.Ok(await PersonaEndpoint.ToProfileDtoAsync(pages, persona, profile, canSpeak: false));
    }

    /// <summary>
    /// The approval queue: characters waiting on a first approval, plus approved ones whose page
    /// has been edited above the revision that was signed off.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/personas/pending")]
    public async Task<IResult> ListPendingAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaPageService pages,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.ApprovePersonas) is { } denied)
            return denied;

        var profiles = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId &&
                        (p.ApprovalState == PersonaApprovalState.Submitted ||
                         p.ApprovalState == PersonaApprovalState.Approved))
            .ToListAsync();

        if (profiles.Count == 0) return Results.Ok(new List<PersonaGuildProfileDto>());

        var personaIds = profiles.Select(p => p.PersonaId).ToList();
        var rows = await ctx.Set<Domain.Entity.Persona>().AsNoTracking().Where(p => personaIds.Contains(p.Id)).ToListAsync();

        var dtos = new List<PersonaGuildProfileDto>();
        foreach (var profile in profiles)
        {
            if (profile.ApprovalState == PersonaApprovalState.Approved && !await HasPendingEditAsync(pages, profile))
                continue;

            var persona = rows.FirstOrDefault(p => p.Id == profile.PersonaId);
            if (persona is null) continue;

            dtos.Add(await PersonaEndpoint.ToProfileDtoAsync(pages, persona, profile, canSpeak: false));
        }

        return Results.Ok(dtos);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Autoproxy

    /// <summary>The caller's autoproxy state in one channel.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/channels/{channelId}/autoproxy")]
    public async Task<IResult> GetAutoproxyAsync(string guildId, string channelId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.UsePersonas) is { } denied)
            return denied;

        if (!await ctx.Channels.AnyAsync(c => c.Id == channelId && c.GuildId == guildId))
            return Results.NotFound();

        var state = await ctx.Set<PersonaAutoproxyState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ChannelId == channelId && a.UserId == userId);

        return Results.Ok(new AutoproxyDto
        {
            GuildId = guildId,
            ChannelId = channelId,
            Mode = state?.Mode ?? AutoproxyMode.Off,
            PersonaId = state?.PersonaId,
        });
    }

    /// <summary>
    /// Sets the caller's autoproxy state for one channel. Server-side rather than client
    /// configuration, so it works identically on every client and survives one that has never
    /// heard of personas.
    /// </summary>
    [WolverinePut("/api/v1/guilds/{guildId}/channels/{channelId}/autoproxy")]
    public async Task<IResult> SetAutoproxyAsync(string guildId, string channelId, SetAutoproxyDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(permissionService, ctx, guildId, userId, ModulePermissions.UsePersonas) is { } denied)
            return denied;

        if (!await ctx.Channels.AnyAsync(c => c.Id == channelId && c.GuildId == guildId))
            return Results.NotFound();

        string? personaId = null;
        if (dto.Mode == AutoproxyMode.Pinned)
        {
            if (string.IsNullOrWhiteSpace(dto.PersonaId))
                return Results.BadRequest("personaId is required for Pinned.");

            var usable = await personas.GetUsablePersonasAsync(userId, guildId);
            var chosen = usable.FirstOrDefault(p => p.PersonaId == dto.PersonaId);
            if (chosen is null) return Results.Forbid();

            var requiresApproval = await personas.RequiresApprovalAsync(guildId);
            if (await personas.DenyReasonAsync(userId, guildId, chosen, requiresApproval) is not null)
                return Results.Forbid();

            personaId = dto.PersonaId;
        }

        var state = await ctx.Set<PersonaAutoproxyState>()
            .FirstOrDefaultAsync(a => a.ChannelId == channelId && a.UserId == userId);

        if (state is null)
        {
            state = PersonaAutoproxyState.Create(new CreatePersonaAutoproxyStateParams
            {
                GuildId = guildId,
                ChannelId = channelId,
                UserId = userId,
            });

            ctx.Set<PersonaAutoproxyState>().Add(state);
        }

        state.Set(dto.Mode, personaId);

        // To the caller and nobody else: this is what the composer on their other device is
        // drawing, and it says which character they are speaking as rather than which they own.
        await realtime.AutoproxyChangedAsync(userId, guildId, channelId, state.Mode, state.PersonaId);

        return Results.Ok(new AutoproxyDto
        {
            GuildId = guildId,
            ChannelId = channelId,
            Mode = state.Mode,
            PersonaId = state.PersonaId,
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Long enough to explain a rejection, short enough not to be the review itself.</summary>
    private const int MaxReasonLength = 1000;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Who may write a profile: the owner of a personal persona, or anybody holding
    /// ManageAnyPersona for one the guild owns.
    /// </summary>
    internal static async Task<IResult?> AuthorizeWriteAsync(
        GuildPermissionService permissionService, string guildId, string userId, Domain.Entity.Persona persona)
    {
        if (persona.Scope == PersonaScope.User)
        {
            if (persona.OwnerUserId != userId) return Results.Forbid();

            return await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.UsePersonas)
                ? null
                : Results.Forbid();
        }

        if (persona.OwnerGuildId != guildId) return Results.NotFound();

        return await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageAnyPersona)
            ? null
            : Results.Forbid();
    }

    private static async Task<(Domain.Entity.Persona?, PersonaGuildProfile?)> LoadAsync(
        MicroserviceContext ctx, string guildId, string personaId)
    {
        var profile = await ctx.Set<PersonaGuildProfile>()
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PersonaId == personaId);
        if (profile is null) return (null, null);

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        return (persona, profile);
    }

    internal static async Task<bool> CanSpeakAsync(
        PersonaService personas, string userId, string guildId, string personaId)
    {
        var usable = await personas.GetUsablePersonasAsync(userId, guildId);
        var chosen = usable.FirstOrDefault(p => p.PersonaId == personaId);
        if (chosen is null) return false;

        var requiresApproval = await personas.RequiresApprovalAsync(guildId);
        return await personas.DenyReasonAsync(userId, guildId, chosen, requiresApproval) is null;
    }

    private static async Task<bool> HasPendingEditAsync(PersonaPageService pages, PersonaGuildProfile profile)
    {
        if (profile.WikiPageId is null) return false;

        return profile.HasUnapprovedChanges(await pages.LatestRevisionNumberAsync(profile.WikiPageId));
    }
}
