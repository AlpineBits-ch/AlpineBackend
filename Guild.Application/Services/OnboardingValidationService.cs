using Guild.Application.Dtos.Request;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Structural and security validation for onboarding configs. Lives outside the endpoint because
/// three call sites need it: the first-party PUT, the Discord-compatible bot endpoint, and the
/// Discord importer - an imported prompt must not be able to smuggle in a role the first-party
/// endpoint would have rejected.
/// </summary>
public class OnboardingValidationService(MicroserviceContext ctx, GuildPermissionService permissionService)
{
    public const int MaxRulesTextLength = 4000;
    public const int MaxDefaultChannels = 25;
    public const int MaxPrompts = 10;
    public const int MaxOptionsPerPrompt = 25;
    public const int MaxTargetsPerOption = 10;
    public const int MaxTitleLength = 100;
    public const int MaxDescriptionLength = 100;

    /// <summary>
    /// Roles carrying any of these may never be handed out by a prompt option: picking an option is
    /// self-service role assignment with no moderator in the loop, so a config referencing one of
    /// these is a privilege-escalation path rather than a customization choice.
    ///
    /// Checked when the config is saved AND again when a member actually answers - a role that was
    /// harmless when the prompt was written can be granted moderation powers afterwards.
    /// </summary>
    public const Permissions PrivilegedPermissions =
        Permissions.Superadmin | Permissions.ManageGuild | Permissions.ManagePermissions |
        Permissions.ManageChannel | Permissions.KickMembers | Permissions.BanMembers |
        Permissions.ModerateMembers | Permissions.ViewAuditLog | Permissions.ManageEmojis |
        Permissions.ManageEvents | Permissions.EditAnyMessage | Permissions.DeleteAnyMessage |
        Permissions.ManageAnyThread | Permissions.EditAnyWikiPage | Permissions.DeleteWikiPages |
        Permissions.ManageWikiStructure | Permissions.ManageWikiRevisions;

    /// <summary>Returns null when the config is valid, otherwise a client-facing reason.</summary>
    public async Task<string?> ValidateAsync(string guildId, string actorUserId, UpdateOnboardingConfigDto dto)
    {
        if (dto.RulesText is { Length: > MaxRulesTextLength })
            return $"RulesText may not exceed {MaxRulesTextLength} characters.";

        var hasJoinFlowPrompt = dto.Prompts.Any(p => p.InOnboarding);
        if (dto.Enabled && string.IsNullOrWhiteSpace(dto.RulesText) && !hasJoinFlowPrompt)
            return "Enabling onboarding requires RulesText or at least one prompt shown during onboarding.";

        var defaultChannelIds = dto.DefaultChannelIds.Distinct().ToList();
        if (defaultChannelIds.Count > MaxDefaultChannels)
            return $"At most {MaxDefaultChannels} default channels may be configured.";

        if (dto.Prompts.Count > MaxPrompts)
            return $"At most {MaxPrompts} prompts may be configured.";

        // Everything the config references, resolved in two queries rather than per-option.
        var referencedChannelIds = defaultChannelIds
            .Concat(dto.Prompts.SelectMany(p => p.Options).SelectMany(o => o.ChannelIds))
            .Distinct()
            .ToList();

        var knownChannelIds = referencedChannelIds.Count == 0
            ? []
            : await ctx.Channels.AsNoTracking()
                .Where(c => c.GuildId == guildId && referencedChannelIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToHashSetAsync();

        var unknownChannels = referencedChannelIds.Where(id => !knownChannelIds.Contains(id)).ToList();
        if (unknownChannels.Count > 0)
            return $"Unknown channel(s) for this guild: {string.Join(", ", unknownChannels)}.";

        var referencedRoleIds = dto.Prompts.SelectMany(p => p.Options).SelectMany(o => o.RoleIds).Distinct().ToList();
        var roles = referencedRoleIds.Count == 0
            ? []
            : await ctx.Roles.AsNoTracking()
                .Where(r => r.GuildId == guildId && referencedRoleIds.Contains(r.Id))
                .ToListAsync();

        var unknownRoles = referencedRoleIds.Where(id => roles.All(r => r.Id != id)).ToList();
        if (unknownRoles.Count > 0)
            return $"Unknown role(s) for this guild: {string.Join(", ", unknownRoles)}.";

        foreach (var prompt in dto.Prompts)
        {
            if (string.IsNullOrWhiteSpace(prompt.Title))
                return "Every prompt needs a title.";

            if (prompt.Title.Length > MaxTitleLength)
                return $"Prompt titles may not exceed {MaxTitleLength} characters.";

            if (prompt.Options.Count == 0)
                return $"Prompt '{prompt.Title}' needs at least one option.";

            if (prompt.Options.Count > MaxOptionsPerPrompt)
                return $"Prompt '{prompt.Title}' may not have more than {MaxOptionsPerPrompt} options.";

            if (prompt.Required && !prompt.InOnboarding)
                return $"Prompt '{prompt.Title}' is required but never shown during onboarding, so it could never be answered.";

            foreach (var option in prompt.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Title))
                    return $"Every option of prompt '{prompt.Title}' needs a title.";

                if (option.Title.Length > MaxTitleLength)
                    return $"Option titles may not exceed {MaxTitleLength} characters.";

                if (option.Description is { Length: > MaxDescriptionLength })
                    return $"Option descriptions may not exceed {MaxDescriptionLength} characters.";

                var roleIds = option.RoleIds.Distinct().ToList();
                var channelIds = option.ChannelIds.Distinct().ToList();

                if (roleIds.Count + channelIds.Count == 0)
                    return $"Option '{option.Title}' grants nothing - give it at least one role or channel.";

                if (roleIds.Count > MaxTargetsPerOption || channelIds.Count > MaxTargetsPerOption)
                    return $"Option '{option.Title}' may reference at most {MaxTargetsPerOption} roles and {MaxTargetsPerOption} channels.";

                foreach (var roleId in roleIds)
                {
                    var role = roles.First(r => r.Id == roleId);

                    if (role.Type == RoleType.Everyone)
                        return "The @everyone role cannot be granted by an onboarding option - every member already has it.";

                    if (IsPrivileged(role))
                        return $"Role '{role.Name}' carries moderation or management permissions and cannot be handed out by onboarding.";

                    if (!await permissionService.CanManageRoleAsync(actorUserId, guildId, roleId))
                        return $"Role '{role.Name}' is positioned at or above your highest role, so you cannot wire it up.";
                }
            }
        }

        return null;
    }

    public static bool IsPrivileged(Role role) => (role.Permissions & PrivilegedPermissions) != 0;

    /// <summary>
    /// Apply-time re-check: returns the subset of the requested roles that may still be granted.
    /// A role that gained privileged permissions since the prompt was configured is dropped rather
    /// than failing the member's join - the rest of the option still applies.
    /// </summary>
    public async Task<List<string>> FilterGrantableRolesAsync(string guildId, IReadOnlyCollection<string> roleIds)
    {
        if (roleIds.Count == 0) return [];

        // The privilege check runs in memory on purpose: Permissions is a ulong flag enum stored as
        // numeric, and Postgres has no `&` operator for numeric, so translating the bitmask test to
        // SQL fails at runtime (42883). Roles per guild are few, and this is already scoped to the
        // handful an option references.
        var candidates = await ctx.Roles.AsNoTracking()
            .Where(r => r.GuildId == guildId && roleIds.Contains(r.Id) && r.Type != RoleType.Everyone)
            .Select(r => new { r.Id, r.Permissions })
            .ToListAsync();

        return candidates
            .Where(r => (r.Permissions & PrivilegedPermissions) == 0)
            .Select(r => r.Id)
            .ToList();
    }
}
