using Import.Application.Discord;

namespace Import.Application.Mapping;

/// <summary>
/// The non-permission half of a Discord role -&gt; Echo role mapping: the badge, which needs a URL
/// assembled, and the mutual exclusion Echo's Role aggregate enforces on it.
/// </summary>
public static class DiscordRoleMapper
{
    /// <summary>Discord's CDN host for role icons.</summary>
    private const string RoleIconCdnBase = "https://cdn.discordapp.com/role-icons";

    /// <summary>Turns Discord's role <c>icon</c> hash into the CDN URL Echo stores.</summary>
    public static string? IconUrl(DiscordRolePayload role) =>
        string.IsNullOrWhiteSpace(role.Icon) ? null : $"{RoleIconCdnBase}/{role.Id}/{role.Icon}.png";

    /// <summary>The role's emoji badge, or null when it has an icon instead.</summary>
    public static string? UnicodeEmoji(DiscordRolePayload role) =>
        IconUrl(role) is not null || string.IsNullOrWhiteSpace(role.UnicodeEmoji) ? null : role.UnicodeEmoji;
}
