using Guild.Application.Dtos.Request;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Turns a member's prompt answers into actual roles and channel visibility, and takes them back
/// when the member changes their mind. Shared by the join flow (POST /onboarding/accept) and the
/// post-join "Channels &amp; Roles" screen (PUT /onboarding/me/responses), which differ only in
/// whether required prompts must be answered.
/// </summary>
public class OnboardingGrantService(MicroserviceContext ctx, OnboardingValidationService validation)
{
    /// <summary>Validates a set of answers against the guild's prompts. Returns null when valid,
    /// otherwise a client-facing reason.</summary>
    /// <param name="requireRequiredPrompts">True for the join flow and for post-join edits alike -
    /// only false internally when a caller has already established the member is exempt.</param>
    public async Task<string?> ValidateResponsesAsync(string guildId, List<OnboardingPromptResponseDto> responses,
        bool requireRequiredPrompts = true)
    {
        var prompts = await ctx.Set<GuildOnboardingPrompt>()
            .AsNoTracking()
            .Include(p => p.Options)
            .Where(p => p.GuildId == guildId)
            .ToListAsync();

        var seenPromptIds = new HashSet<string>();

        foreach (var response in responses)
        {
            var prompt = prompts.FirstOrDefault(p => p.Id == response.PromptId);
            if (prompt is null)
                return $"Unknown prompt '{response.PromptId}' for this guild.";

            if (!seenPromptIds.Add(response.PromptId))
                return $"Prompt '{response.PromptId}' answered more than once.";

            var optionIds = response.OptionIds.Distinct().ToList();

            if (prompt.SingleSelect && optionIds.Count > 1)
                return $"Prompt '{prompt.Title}' only allows one option.";

            foreach (var optionId in optionIds)
            {
                if (prompt.Options.All(o => o.Id != optionId))
                    return $"Option '{optionId}' does not belong to prompt '{prompt.Title}'.";
            }
        }

        if (requireRequiredPrompts)
        {
            var answered = responses
                .Where(r => r.OptionIds.Count > 0)
                .Select(r => r.PromptId)
                .ToHashSet();

            var missing = prompts.FirstOrDefault(p => p.Required && p.InOnboarding && !answered.Contains(p.Id));
            if (missing is not null)
                return $"Prompt '{missing.Title}' ({missing.Id}) is required.";
        }

        return null;
    }

    /// <summary>
    /// Makes the member's grants match <paramref name="selectedOptionIds"/> exactly: grants what is
    /// newly selected, revokes what is no longer selected, leaves the rest alone. Idempotent.
    /// Callers are responsible for invalidating the member's permission cache afterwards and for
    /// committing (Wolverine's middleware does the latter).
    /// </summary>
    public async Task ApplySelectionAsync(string guildId, GuildMember member, IReadOnlyCollection<string> selectedOptionIds)
    {
        var existingResponses = await ctx.Set<GuildMemberOnboardingResponse>()
            .Where(r => r.MemberId == member.Id)
            .ToListAsync();

        var existingOptionIds = existingResponses.Select(r => r.OptionId).ToHashSet();
        var addedOptionIds = selectedOptionIds.Where(id => !existingOptionIds.Contains(id)).ToList();
        var removedOptionIds = existingOptionIds.Where(id => !selectedOptionIds.Contains(id)).ToList();

        if (addedOptionIds.Count > 0)
            await GrantAsync(guildId, member, addedOptionIds);

        if (removedOptionIds.Count > 0)
            await RevokeAsync(guildId, member, removedOptionIds, selectedOptionIds);

        foreach (var response in existingResponses.Where(r => removedOptionIds.Contains(r.OptionId)))
            ctx.Set<GuildMemberOnboardingResponse>().Remove(response);
    }

    private async Task GrantAsync(string guildId, GuildMember member, List<string> optionIds)
    {
        var options = await ctx.Set<GuildOnboardingPromptOption>()
            .AsNoTracking()
            .Where(o => optionIds.Contains(o.Id))
            .ToListAsync();

        var heldRoleIds = await ctx.RoleMembers
            .Where(rm => rm.MemberId == member.Id)
            .Select(rm => rm.RoleId)
            .ToHashSetAsync();

        // A member-scoped overwrite is looked up as "the" overwrite for that member on that channel
        // (see GuildPermissionService.ApplyOverwrites), so there must never be two. If one already
        // exists we leave it alone rather than adding a competing row.
        var heldOverwriteChannelIds = await ctx.Set<ChannelPermission>()
            .Where(p => p.MemberId == member.Id && p.ChannelId != null)
            .Select(p => p.ChannelId!)
            .ToHashSetAsync();

        var existingGrants = await ctx.Set<GuildOnboardingGrant>()
            .Where(g => g.MemberId == member.Id)
            .ToListAsync();

        // What onboarding already owns for this member. Anything the member holds that is NOT in
        // here was assigned some other way (by a moderator, by the join flow, by a bot) and must
        // never be claimed - claiming it would let a later deselect take it away.
        var ownedRoleIds = existingGrants.Where(g => g.RoleId is not null).Select(g => g.RoleId!).ToHashSet();
        var ownedOverwriteByChannel = existingGrants
            .Where(g => g.ChannelId is not null && g.ChannelPermissionId is not null)
            .GroupBy(g => g.ChannelId!)
            .ToDictionary(g => g.Key, g => g.First().ChannelPermissionId!);

        var now = DateTimeOffset.UtcNow;

        foreach (var option in options)
        {
            // Re-checked here and not just at config time: a role can gain moderation permissions
            // between the prompt being written and a member answering it.
            var grantableRoleIds = await validation.FilterGrantableRolesAsync(guildId, option.RoleIds);

            foreach (var roleId in grantableRoleIds)
            {
                if (ownedRoleIds.Contains(roleId))
                {
                    // Another onboarding option already granted it - record this option's claim too
                    // so deselecting only one of them keeps the role.
                    ctx.Set<GuildOnboardingGrant>().Add(
                        GuildOnboardingGrant.ForRole(guildId, member.Id, option.Id, roleId));
                    continue;
                }

                if (heldRoleIds.Contains(roleId)) continue;

                ctx.RoleMembers.Add(new RoleMember
                {
                    Id = RoleMember.GenerateId(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    RoleId = roleId,
                    MemberId = member.Id,
                });
                heldRoleIds.Add(roleId);
                ownedRoleIds.Add(roleId);

                ctx.Set<GuildOnboardingGrant>().Add(
                    GuildOnboardingGrant.ForRole(guildId, member.Id, option.Id, roleId));
            }

            foreach (var channelId in option.ChannelIds.Distinct())
            {
                if (ownedOverwriteByChannel.TryGetValue(channelId, out var existingOverwriteId))
                {
                    ctx.Set<GuildOnboardingGrant>().Add(
                        GuildOnboardingGrant.ForChannel(guildId, member.Id, option.Id, channelId, existingOverwriteId));
                    continue;
                }

                if (heldOverwriteChannelIds.Contains(channelId)) continue;

                var overwrite = new ChannelPermission
                {
                    Id = ChannelPermission.GenerateId(),
                    ChannelId = channelId,
                    MemberId = member.Id,
                    AllowPermissions = Permissions.ViewChannel,
                    DenyPermissions = Permissions.None,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                ctx.Set<ChannelPermission>().Add(overwrite);
                heldOverwriteChannelIds.Add(channelId);
                ownedOverwriteByChannel[channelId] = overwrite.Id;

                ctx.Set<GuildOnboardingGrant>().Add(
                    GuildOnboardingGrant.ForChannel(guildId, member.Id, option.Id, channelId, overwrite.Id));
            }

            ctx.Set<GuildMemberOnboardingResponse>().Add(new GuildMemberOnboardingResponse
            {
                MemberId = member.Id,
                OptionId = option.Id,
                PromptId = option.PromptId,
                CreatedAt = now,
            });
        }
    }

    private async Task RevokeAsync(string guildId, GuildMember member, List<string> removedOptionIds,
        IReadOnlyCollection<string> stillSelectedOptionIds)
    {
        var grants = await ctx.Set<GuildOnboardingGrant>()
            .Where(g => g.MemberId == member.Id)
            .ToListAsync();

        var doomed = grants.Where(g => removedOptionIds.Contains(g.OptionId)).ToList();
        if (doomed.Count == 0) return;

        // Anything a still-selected option also granted stays - deselecting "Gaming" must not take
        // away a role "Streaming" is still handing out.
        var survivingRoleIds = grants
            .Where(g => stillSelectedOptionIds.Contains(g.OptionId) && g.RoleId is not null)
            .Select(g => g.RoleId!)
            .ToHashSet();

        var survivingChannelIds = grants
            .Where(g => stillSelectedOptionIds.Contains(g.OptionId) && g.ChannelId is not null)
            .Select(g => g.ChannelId!)
            .ToHashSet();

        var roleIdsToDrop = doomed
            .Where(g => g.RoleId is not null && !survivingRoleIds.Contains(g.RoleId))
            .Select(g => g.RoleId!)
            .Distinct()
            .ToList();

        if (roleIdsToDrop.Count > 0)
        {
            var roleMembers = await ctx.RoleMembers
                .Where(rm => rm.MemberId == member.Id && roleIdsToDrop.Contains(rm.RoleId))
                .ToListAsync();

            ctx.RoleMembers.RemoveRange(roleMembers);
        }

        var overwriteIdsToDrop = doomed
            .Where(g => g.ChannelId is not null && g.ChannelPermissionId is not null
                        && !survivingChannelIds.Contains(g.ChannelId))
            .Select(g => g.ChannelPermissionId!)
            .Distinct()
            .ToList();

        if (overwriteIdsToDrop.Count > 0)
        {
            var overwrites = await ctx.Set<ChannelPermission>()
                .Where(p => overwriteIdsToDrop.Contains(p.Id))
                .ToListAsync();

            ctx.Set<ChannelPermission>().RemoveRange(overwrites);
        }

        ctx.Set<GuildOnboardingGrant>().RemoveRange(doomed);
    }
}
