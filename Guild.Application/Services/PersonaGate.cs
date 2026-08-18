using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The three checks every guild-scoped persona route makes, in the order AbsenceEndpoint
/// established: the module, then membership, then the permission.
/// </summary>
public static class PersonaGate
{
    /// <summary>Returns the failing result, or null when the caller may proceed.</summary>
    /// <param name="permissions">The permission resolver.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="guildId">The guild the route is scoped to.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="required">The module permission the route needs.</param>
    /// <param name="feature">The module that owns it, which defaults to personas.</param>
    public static async Task<IResult?> CheckAsync(
        GuildPermissionService permissions, MicroserviceContext ctx,
        string guildId, string userId, ModulePermissions required,
        GuildFeatures feature = GuildFeatures.Personas)
    {
        if (!await permissions.IsFeatureEnabledAsync(guildId, feature))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        if (!await permissions.CanUserPerformActionOnGuildAsync(userId, guildId, required))
            return Results.Forbid();

        return null;
    }

    /// <summary>The module and membership halves alone, for routes whose permission depends on
    /// whether the caller owns the persona.</summary>
    public static async Task<IResult?> CheckMembershipAsync(
        GuildPermissionService permissions, MicroserviceContext ctx, string guildId, string userId)
    {
        if (!await permissions.IsFeatureEnabledAsync(guildId, GuildFeatures.Personas))
            return Results.Forbid();

        return await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId)
            ? null
            : Results.Forbid();
    }
}
