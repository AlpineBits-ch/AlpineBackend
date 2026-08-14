using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>What a cleanup pass actually changed, for logging and for the tests to assert on.</summary>
public record RoleReferenceCleanup(
    int OnboardingOptionsUpdated,
    int OnboardingGrantsRemoved,
    int ChoresDetached,
    int ChoresPaused);

/// <summary>Removes the two references to a role that the database cannot remove for us.</summary>
public class RoleReferenceCleanupService(MicroserviceContext ctx, ILogger<RoleReferenceCleanupService> logger)
{
    /// <summary>Stages the cleanup for a role that has just been deleted.</summary>
    public async Task<RoleReferenceCleanup> CleanupAsync(string guildId, string roleId)
    {
        var optionsUpdated = await CleanOnboardingOptionsAsync(guildId, roleId);
        var grantsRemoved = await CleanOnboardingGrantsAsync(guildId, roleId);
        var (detached, paused) = await CleanChoresAsync(guildId, roleId);

        var result = new RoleReferenceCleanup(optionsUpdated, grantsRemoved, detached, paused);

        if (optionsUpdated + grantsRemoved + detached > 0)
        {
            logger.LogInformation(
                "Cleaned up references to deleted role {RoleId} in guild {GuildId}: {Options} onboarding "
                + "option(s), {Grants} grant(s), {Chores} chore(s) detached of which {Paused} paused",
                roleId, guildId, optionsUpdated, grantsRemoved, detached, paused);
        }

        return result;
    }

    /// <summary>
    /// Loaded per guild and filtered in memory rather than with a containment predicate.
    /// </summary>
    private async Task<int> CleanOnboardingOptionsAsync(string guildId, string roleId)
    {
        var promptIds = await ctx.Set<GuildOnboardingPrompt>()
            .Where(p => p.GuildId == guildId)
            .Select(p => p.Id)
            .ToListAsync();

        if (promptIds.Count == 0) return 0;

        var options = await ctx.Set<GuildOnboardingPromptOption>()
            .Where(o => promptIds.Contains(o.PromptId))
            .ToListAsync();

        var updated = 0;
        foreach (var option in options.Where(o => o.RoleIds.Contains(roleId)))
        {
            // Reassigned rather than mutated in place: the list is mapped to a text[] column, and a
            // mutation of the same instance is not reliably picked up as a change.
            option.RoleIds = option.RoleIds.Where(id => id != roleId).ToList();
            option.UpdatedAt = DateTimeOffset.UtcNow;
            updated++;
        }

        return updated;
    }

    /// <summary>The grant rows recording that onboarding handed this role out.</summary>
    private async Task<int> CleanOnboardingGrantsAsync(string guildId, string roleId)
    {
        var grants = await ctx.Set<GuildOnboardingGrant>()
            .Where(g => g.GuildId == guildId && g.RoleId == roleId)
            .ToListAsync();

        if (grants.Count == 0) return 0;

        ctx.Set<GuildOnboardingGrant>().RemoveRange(grants);
        return grants.Count;
    }

    private async Task<(int Detached, int Paused)> CleanChoresAsync(string guildId, string roleId)
    {
        var chores = await ctx.Chores
            .Where(c => c.GuildId == guildId && c.RotationRoleId == roleId)
            .ToListAsync();

        if (chores.Count == 0) return (0, 0);

        var paused = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var chore in chores)
        {
            chore.RotationRoleId = null;
            chore.UpdatedAt = now;

            // A chore that also named a fixed assignee still has somebody to give it to - losing the
            // rotation role degrades it to "always theirs", which is a working chore and not a broken
            // one. Only the chore with nothing left to rotate over gets paused.
            if (chore.FixedAssigneeUserId is not null) continue;

            chore.IsPaused = true;
            paused++;
        }

        return (chores.Count, paused);
    }
}
