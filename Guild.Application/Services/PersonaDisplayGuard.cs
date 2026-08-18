using System.Text.RegularExpressions;
using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>The shape rules a persona's display data has to satisfy.</summary>
public static class PersonaLimits
{
    /// <summary>Matches the webhook username cap, which is the other per-message display override.</summary>
    public const int MaxNameLength = 80;

    public const int MaxTagLength = 32;
    public const int MaxPronounsLength = 40;
    public const int MaxShortBioLength = 1000;
    public const int MaxAvatarUrlLength = 512;

    /// <summary>Long enough for <c>Mayor Cogsgrove:</c>, short enough not to be a message.</summary>
    public const int MaxProxyAffixLength = 32;

    /// <summary>Nothing capped personas before, and a free display name with no ceiling is an abuse
    /// primitive - PluralKit caps a system at 1000 members for the same reason.</summary>
    public const int MaxPersonasPerUser = 500;

    public const int MaxProfilesPerUserPerGuild = 200;
    public const int MaxPersonasPerGuild = 500;
}

/// <summary>
/// The impersonation controls a persona needs and a webhook never had: length caps, an avatar
/// allowlist restricted to instance-hosted media, and a collision check against the names a member
/// already trusts in that guild.
/// </summary>
public class PersonaDisplayGuard(MicroserviceContext ctx)
{
    private static readonly Regex HexColor =
        new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Checks a persona's own fields, returning the first problem or null.</summary>
    public static string? ValidateCore(string? name, string? avatarUrl, string? pronouns, string? color, string? shortBio)
    {
        if (name is not null)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return "Persona name cannot be empty.";
            if (trimmed.Length > PersonaLimits.MaxNameLength)
                return $"Persona name cannot exceed {PersonaLimits.MaxNameLength} characters.";
            if (trimmed.Any(char.IsControl)) return "Persona name cannot contain control characters.";
        }

        if (!string.IsNullOrWhiteSpace(pronouns) && pronouns.Length > PersonaLimits.MaxPronounsLength)
            return $"Pronouns cannot exceed {PersonaLimits.MaxPronounsLength} characters.";

        if (!string.IsNullOrWhiteSpace(shortBio) && shortBio.Length > PersonaLimits.MaxShortBioLength)
            return $"Short bio cannot exceed {PersonaLimits.MaxShortBioLength} characters.";

        if (!string.IsNullOrWhiteSpace(color) && !HexColor.IsMatch(color))
            return "Persona colour must be a hex colour such as #4F8A6B.";

        return ValidateAvatar(avatarUrl);
    }

    /// <summary>Checks the per-guild overrides, returning the first problem or null.</summary>
    public static string? ValidateProfile(string? displayName, string? avatarUrl, string? tag,
        string? proxyPrefix, string? proxySuffix)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            if (displayName.Trim().Length > PersonaLimits.MaxNameLength)
                return $"Display name cannot exceed {PersonaLimits.MaxNameLength} characters.";
            if (displayName.Any(char.IsControl)) return "Display name cannot contain control characters.";
        }

        if (!string.IsNullOrWhiteSpace(tag) && tag.Trim().Length > PersonaLimits.MaxTagLength)
            return $"Tag cannot exceed {PersonaLimits.MaxTagLength} characters.";

        foreach (var affix in new[] { proxyPrefix, proxySuffix })
        {
            if (string.IsNullOrWhiteSpace(affix)) continue;
            if (affix.Length > PersonaLimits.MaxProxyAffixLength)
                return $"Proxy prefix and suffix cannot exceed {PersonaLimits.MaxProxyAffixLength} characters.";
            if (affix.Any(char.IsControl)) return "Proxy prefix and suffix cannot contain control characters.";
        }

        // A prefix of only whitespace matches every message the user sends.
        if (proxyPrefix is not null && proxyPrefix.Length > 0 && proxyPrefix.Trim().Length == 0)
            return "Proxy prefix cannot be only whitespace.";

        return ValidateAvatar(avatarUrl);
    }

    /// <summary>
    /// An avatar has to be media this instance serves. Anything else lets a member point a
    /// character at an arbitrary host, which is a tracking pixel on every message it sends as well
    /// as an impersonation vector.
    /// </summary>
    public static string? ValidateAvatar(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;

        if (avatarUrl.Length > PersonaLimits.MaxAvatarUrlLength)
            return $"Avatar URL cannot exceed {PersonaLimits.MaxAvatarUrlLength} characters.";

        return IsInstanceHostedMedia(avatarUrl)
            ? null
            : "Avatar URL must point at media hosted by this instance.";
    }

    /// <summary>
    /// Whether a URL is served by this instance: a rooted path, one of the instance's own
    /// hostnames, or the configured object-storage host that every upload endpoint hands back.
    /// </summary>
    public static bool IsInstanceHostedMedia(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0) return false;

        // An app-relative path cannot leave the instance, and it is what WikiPage.CoverUrl already
        // accepts.
        if (trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal)) return true;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        if (InstanceLinkHosts.IsInstanceHost(uri)) return true;

        return StorageHosts().Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> StorageHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storage = Env.StorageConfiguration;

        foreach (var candidate in new[] { storage.PublicUrl, storage.ServiceUrl })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Uri.TryCreate(candidate.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) &&
                !string.IsNullOrEmpty(uri.Host))
            {
                hosts.Add(uri.Host);
            }
        }

        return hosts;
    }

    /// <summary>
    /// The name a persona will actually render under in a guild, which is the per-guild override
    /// when there is one.
    /// </summary>
    public static string EffectiveName(Persona persona, PersonaGuildProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.DisplayName) ? persona.Name : profile.DisplayName!;

    /// <summary>
    /// Whether <paramref name="displayName"/> is already a member nickname or a role name in this
    /// guild. Both are names people read as identity, so a character wearing one is impersonation
    /// whatever the client renders.
    /// </summary>
    public async Task<string?> FindNameCollisionAsync(string guildId, string displayName)
    {
        var candidate = displayName.Trim();
        if (candidate.Length == 0) return null;

        var nickname = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && m.Nickname != null && m.Nickname.ToLower() == candidate.ToLower())
            .Select(m => m.Nickname)
            .FirstOrDefaultAsync();

        if (nickname is not null) return $"a member nickname in this guild ({nickname})";

        var role = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId && r.Name.ToLower() == candidate.ToLower())
            .Select(r => r.Name)
            .FirstOrDefaultAsync();

        return role is null ? null : $"a role name in this guild ({role})";
    }

    /// <summary>
    /// Runs <see cref="FindNameCollisionAsync"/> against every guild a persona is adopted into,
    /// because renaming the persona itself changes what it renders as in all of them at once.
    /// </summary>
    public async Task<string?> FindNameCollisionAcrossProfilesAsync(string personaId, string newName)
    {
        var guildIds = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.PersonaId == personaId && p.DisplayName == null)
            .Select(p => p.GuildId)
            .ToListAsync();

        foreach (var guildId in guildIds)
        {
            if (await FindNameCollisionAsync(guildId, newName) is { } collision)
                return $"{collision} in guild {guildId}";
        }

        return null;
    }
}
