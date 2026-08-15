using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Vanity-slug resolution and the entitlement gate in front of it.</summary>
public class VanityUrlService(MicroserviceContext ctx, ILogger<VanityUrlService> logger, EntitlementResolver? entitlements = null)
{
    /// <summary>Whether the guild's plan currently covers a vanity URL.</summary>
    /// <param name="strict">What an unreadable answer means.</param>
    public async Task<bool> IsEntitledAsync(string guildId, bool strict, CancellationToken ct = default)
    {
        // No resolver at all is a host with no billing wired up - self-hosted, or a test.
        if (entitlements is null) return true;

        try
        {
            var set = await entitlements.ResolveAsync(EntitlementSubject.ForGuild(guildId), ct);
            return set.Flag(EntitlementKeys.GuildVanityUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not resolve the vanity-URL entitlement for guild {Guild}", guildId);
            return !strict;
        }
    }

    /// <summary>
    /// The invite a vanity slug leads to, or null if the slug is unknown, the guild's plan no
    /// longer covers it, or the backing invite is gone.
    /// </summary>
    /// <param name="track">Whether the returned invite is change-tracked.</param>
    public async Task<GuildInvite?> ResolveInviteAsync(string slug, bool track = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var normalized = VanitySlug.Normalize(slug);
        if (VanitySlug.Validate(normalized) is not null) return null;

        var guild = await ctx.Guilds.AsNoTracking()
            .Where(g => g.VanityUrl == normalized)
            .Select(g => new { g.Id, g.VanityInviteId })
            .FirstOrDefaultAsync(ct);

        if (guild?.VanityInviteId is null) return null;
        if (!await IsEntitledAsync(guild.Id, strict: false, ct)) return null;

        var query = ctx.GuildInvites.Include(i => i.Guild).Where(i => i.Id == guild.VanityInviteId);
        if (!track) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(ct);
    }
}
