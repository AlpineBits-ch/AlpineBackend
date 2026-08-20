using System.Security.Claims;
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
/// The upload half of a character's avatar, for the row itself and for a guild's override of it.
/// <see cref="PersonaDisplayGuard"/> only accepts instance-hosted media, so these routes are the
/// only way a client can produce a value the persona routes will store.
/// </summary>
[Authorize]
public class PersonaAvatarEndpoint
{
    /// <summary>Above a 400x400 crop with room to spare and well below a raw phone photo: an avatar
    /// is fetched again on every message the character sends.</summary>
    private const long MaxAvatarBytes = 2 * 1024 * 1024;

    /// <summary>An allowlist, and the stored content type is taken from here rather than from the
    /// request: the presigned URL serves the object with the type we recorded, so trusting the
    /// caller's string would let an upload choose how a browser reads its own bytes.</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif",
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The character's own picture

    /// <summary>Replaces the avatar on the character row, which every guild sees unless it overrides it.</summary>
    [WolverinePost("/api/v1/personas/{personaId}/avatar")]
    public async Task<IResult> UploadAsync(string personaId, IFormFile file,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaAvatarService avatars, [NotBody] AuditLogService auditLog,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return Results.NotFound();

        if (await AuthorizeGlobalAsync(permissionService, ctx, persona, userId) is { } denied) return denied;

        var (rejected, contentType) = ValidateFile(file);
        if (rejected is not null) return rejected;

        var url = PersonaAvatarService.PublicUrlFor(personaId, PersonaAvatarService.Version());

        // Validated rather than trusted because it was composed here: it is built from INSTANCE_URL,
        // which an operator writes, and a value with no scheme would persist a URL no client resolves.
        if (PersonaDisplayGuard.ValidateAvatar(url) is { } error) return Results.BadRequest(error);

        await avatars.UploadAsync(file, PersonaAvatarService.GlobalKey(personaId), contentType);

        persona.AvatarUrl = url;

        if (persona.Scope == PersonaScope.Guild && persona.OwnerGuildId is not null)
            auditLog.Log(persona.OwnerGuildId, userId, AuditActionType.PersonaUpdated, persona.Id, new { persona.Name });

        await personas.InvalidatePersonaAsync(persona.Id);
        await realtime.PersonaUpdatedAsync(persona);

        return Results.Ok(PersonaDto.From(persona));
    }

    /// <summary>Clears the avatar on the character row.</summary>
    [WolverineDelete("/api/v1/personas/{personaId}/avatar")]
    public async Task<IResult> DeleteAsync(string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaAvatarService avatars, [NotBody] AuditLogService auditLog,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return Results.NotFound();

        if (await AuthorizeGlobalAsync(permissionService, ctx, persona, userId) is { } denied) return denied;

        if (persona.AvatarUrl is null) return Results.NoContent();

        persona.AvatarUrl = null;

        // Removed before the commit rather than after it, because Wolverine's middleware is what
        // commits and there is no after here. The delete is best-effort for that reason.
        await avatars.DeleteAsync(PersonaAvatarService.GlobalKey(personaId));

        if (persona.Scope == PersonaScope.Guild && persona.OwnerGuildId is not null)
            auditLog.Log(persona.OwnerGuildId, userId, AuditActionType.PersonaUpdated, persona.Id, new { persona.Name });

        await personas.InvalidatePersonaAsync(persona.Id);
        await realtime.PersonaUpdatedAsync(persona);

        return Results.NoContent();
    }

    /// <summary>Serves the character's avatar.</summary>
    [AllowAnonymous]
    [WolverineGet("/api/v1/personas/{personaId}/avatar")]
    public async Task<IResult> GetAsync(string personaId,
        [NotBody] PersonaAvatarService avatars, [NotBody] MicroserviceContext ctx)
    {
        // A row can hold an avatar this route does not serve: the column also accepts a rooted path.
        var hasAvatar = await ctx.Set<Domain.Entity.Persona>().AsNoTracking()
            .AnyAsync(p => p.Id == personaId && p.AvatarUrl != null);

        return hasAvatar
            ? Results.Redirect(avatars.GetPresignedUrl(PersonaAvatarService.GlobalKey(personaId)))
            : Results.NotFound();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // This guild's override

    /// <summary>Replaces the avatar this guild shows for the character, leaving the row alone. The
    /// character has to be adopted here already.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/personas/{personaId}/profile/avatar")]
    public async Task<IResult> UploadProfileAsync(string guildId, string personaId, IFormFile file,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaAvatarService avatars, [NotBody] PersonaPageService pages,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var (persona, profile, failure) = await LoadForWriteAsync(permissionService, ctx, guildId, personaId, userId);
        if (failure is not null) return failure;

        var (rejected, contentType) = ValidateFile(file);
        if (rejected is not null) return rejected;

        var url = PersonaAvatarService.PublicUrlFor(personaId, guildId, PersonaAvatarService.Version());
        if (PersonaDisplayGuard.ValidateAvatar(url) is { } error) return Results.BadRequest(error);

        await avatars.UploadAsync(file, PersonaAvatarService.GuildKey(personaId, guildId), contentType);

        profile!.AvatarUrl = url;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        return await RespondAsync(personas, pages, realtime, persona!, profile, userId, guildId);
    }

    /// <summary>Clears this guild's override, so the character row's avatar shows here again.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/personas/{personaId}/profile/avatar")]
    public async Task<IResult> DeleteProfileAsync(string guildId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaAvatarService avatars, [NotBody] PersonaPageService pages,
        [NotBody] RoleplayRealtimeService realtime, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var (persona, profile, failure) = await LoadForWriteAsync(permissionService, ctx, guildId, personaId, userId);
        if (failure is not null) return failure;

        if (profile!.AvatarUrl is not null)
        {
            profile.AvatarUrl = null;
            profile.UpdatedAt = DateTimeOffset.UtcNow;

            await avatars.DeleteAsync(PersonaAvatarService.GuildKey(personaId, guildId));
        }

        return await RespondAsync(personas, pages, realtime, persona!, profile, userId, guildId);
    }

    /// <summary>Serves this guild's override of the character's avatar.</summary>
    [AllowAnonymous]
    [WolverineGet("/api/v1/guilds/{guildId}/personas/{personaId}/profile/avatar")]
    public async Task<IResult> GetProfileAsync(string guildId, string personaId,
        [NotBody] PersonaAvatarService avatars, [NotBody] MicroserviceContext ctx)
    {
        var hasAvatar = await ctx.Set<PersonaGuildProfile>().AsNoTracking()
            .AnyAsync(p => p.GuildId == guildId && p.PersonaId == personaId && p.AvatarUrl != null);

        return hasAvatar
            ? Results.Redirect(avatars.GetPresignedUrl(PersonaAvatarService.GuildKey(personaId, guildId)))
            : Results.NotFound();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers

    /// <summary>Who may replace the picture on the character row: the owner of a personal persona,
    /// or ManageAnyPersona in the guild that owns a shared one.</summary>
    private static async Task<IResult?> AuthorizeGlobalAsync(
        GuildPermissionService permissionService, MicroserviceContext ctx,
        Domain.Entity.Persona persona, string userId)
    {
        if (persona.Scope == PersonaScope.User)
            return persona.OwnerUserId == userId ? null : Results.NotFound();

        return persona.OwnerGuildId is null
            ? Results.NotFound()
            : await PersonaGate.CheckAsync(
                permissionService, ctx, persona.OwnerGuildId, userId, ModulePermissions.ManageAnyPersona);
    }

    /// <summary>The gate the profile PUT applies, plus the profile itself: an override is only
    /// settable on a character this guild has already adopted.</summary>
    private static async Task<(Domain.Entity.Persona?, PersonaGuildProfile?, IResult?)> LoadForWriteAsync(
        GuildPermissionService permissionService, MicroserviceContext ctx,
        string guildId, string personaId, string userId)
    {
        if (await PersonaGate.CheckMembershipAsync(permissionService, ctx, guildId, userId) is { } blocked)
            return (null, null, blocked);

        var persona = await ctx.Set<Domain.Entity.Persona>().FirstOrDefaultAsync(p => p.Id == personaId);
        if (persona is null) return (null, null, Results.NotFound());

        if (await PersonaProfileEndpoint.AuthorizeWriteAsync(permissionService, guildId, userId, persona) is { } denied)
            return (null, null, denied);

        var profile = await ctx.Set<PersonaGuildProfile>()
            .FirstOrDefaultAsync(p => p.GuildId == guildId && p.PersonaId == personaId);

        return profile is null
            ? (null, null, Results.NotFound())
            : (persona, profile, null);
    }

    private static async Task<IResult> RespondAsync(
        PersonaService personas, PersonaPageService pages, RoleplayRealtimeService realtime,
        Domain.Entity.Persona persona, PersonaGuildProfile profile, string userId, string guildId)
    {
        await personas.InvalidateGuildAsync(guildId);

        var canSpeak = await PersonaProfileEndpoint.CanSpeakAsync(personas, userId, guildId, profile.PersonaId);
        await realtime.ProfileChangedAsync(profile, canSpeak);

        return Results.Ok(await PersonaEndpoint.ToProfileDtoAsync(pages, persona, profile, canSpeak));
    }

    /// <summary>Returns the failing result, or the content type the object will be stored under.</summary>
    private static (IResult? Failure, string ContentType) ValidateFile(IFormFile file)
    {
        if (file is null || file.Length == 0) return (Results.BadRequest("A file is required."), "");

        if (file.Length > MaxAvatarBytes)
            return (Results.BadRequest($"An avatar must be {MaxAvatarBytes / (1024 * 1024)} MB or smaller."), "");

        var contentType = AllowedContentTypes.FirstOrDefault(
            t => string.Equals(t, file.ContentType, StringComparison.OrdinalIgnoreCase));

        return contentType is null
            ? (Results.BadRequest("An avatar must be a PNG, JPEG, WebP or GIF image."), "")
            : (null, contentType);
    }
}
