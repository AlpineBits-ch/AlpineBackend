using Guild.Application.Dtos.Request;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Reading and replacing a guild's onboarding config.</summary>
public class OnboardingConfigService(
    MicroserviceContext ctx,
    OnboardingValidationService validation,
    GuildPermissionService permissionService,
    AuditLogService auditLog)
{
    public async Task<UpdateOnboardingConfigDto> GetAsync(string guildId)
    {
        var config = await ctx.Set<GuildOnboardingConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);
        var prompts = await LoadPromptsAsync(guildId);

        return new UpdateOnboardingConfigDto
        {
            Enabled = config?.Enabled ?? false,
            RulesText = config?.RulesText,
            Mode = config?.Mode ?? OnboardingMode.Default,
            DefaultChannelIds = config?.DefaultChannelIds ?? [],
            Prompts = prompts.Select(ToDto).ToList(),
        };
    }

    /// <summary>Whole-document replace.</summary>
    public async Task<(string? Error, UpdateOnboardingConfigDto? Config)> ReplaceAsync(string guildId,
        string actorUserId, UpdateOnboardingConfigDto dto)
    {
        var error = await validation.ValidateAsync(guildId, actorUserId, dto);
        if (error is not null) return (error, null);

        var config = await ctx.Set<GuildOnboardingConfig>().FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config is null)
        {
            config = new GuildOnboardingConfig { GuildId = guildId };
            ctx.Set<GuildOnboardingConfig>().Add(config);
        }

        var wasEnabled = config.Enabled;
        var rulesTextChanged = config.RulesText != dto.RulesText;

        config.Enabled = dto.Enabled;
        config.RulesText = dto.RulesText;
        config.Mode = dto.Mode;
        config.DefaultChannelIds = dto.DefaultChannelIds.Distinct().ToList();
        config.UpdatedAt = DateTimeOffset.UtcNow;

        var prompts = await ReconcilePromptsAsync(guildId, actorUserId, dto.Prompts);

        // Turning onboarding off releases everyone still sitting on the rules screen - there is no
        // longer a screen for them to accept, so leaving them pending would strand them
        // participation-restricted forever.
        if (wasEnabled && !dto.Enabled)
            await CompletePendingMembersAsync(guildId);

        auditLog.Log(guildId, actorUserId, AuditActionType.OnboardingConfigUpdated, guildId, new
        {
            dto.Enabled,
            Mode = dto.Mode.ToString(),
            RulesTextChanged = rulesTextChanged,
            DefaultChannelCount = config.DefaultChannelIds.Count,
            PromptCount = dto.Prompts.Count,
        });

        return (null, new UpdateOnboardingConfigDto
        {
            Enabled = config.Enabled,
            RulesText = config.RulesText,
            Mode = config.Mode,
            DefaultChannelIds = config.DefaultChannelIds,
            Prompts = prompts.Select(ToDto).ToList(),
        });
    }

    public async Task<List<GuildOnboardingPrompt>> LoadPromptsAsync(string guildId, bool inOnboardingOnly = false)
    {
        var query = ctx.Set<GuildOnboardingPrompt>()
            .AsNoTracking()
            .Include(p => p.Options)
            .Where(p => p.GuildId == guildId);

        if (inOnboardingOnly) query = query.Where(p => p.InOnboarding);

        var prompts = await query.OrderBy(p => p.Position).ToListAsync();
        foreach (var prompt in prompts)
            prompt.Options = prompt.Options.OrderBy(o => o.Position).ToList();

        return prompts;
    }

    public static OnboardingPromptDto ToDto(GuildOnboardingPrompt prompt) => new()
    {
        Id = prompt.Id,
        Title = prompt.Title,
        Type = prompt.Type,
        SingleSelect = prompt.SingleSelect,
        Required = prompt.Required,
        InOnboarding = prompt.InOnboarding,
        Position = prompt.Position,
        Options = prompt.Options.OrderBy(o => o.Position).Select(o => new OnboardingPromptOptionDto
        {
            Id = o.Id,
            Title = o.Title,
            Description = o.Description,
            Emoji = o.Emoji,
            RoleIds = o.RoleIds,
            ChannelIds = o.ChannelIds,
            Position = o.Position,
        }).ToList(),
    };

    /// <summary>Reconciles the stored prompt tree against the payload: known ids update in place,
    /// missing ids are created, stored prompts absent from the payload are deleted.</summary>
    private async Task<List<GuildOnboardingPrompt>> ReconcilePromptsAsync(string guildId, string actorUserId,
        List<OnboardingPromptDto> payload)
    {
        var existing = await ctx.Set<GuildOnboardingPrompt>()
            .Include(p => p.Options)
            .Where(p => p.GuildId == guildId)
            .ToListAsync();

        var keptIds = payload.Where(p => p.Id is not null).Select(p => p.Id!).ToHashSet();
        foreach (var removed in existing.Where(p => !keptIds.Contains(p.Id)))
        {
            ctx.Set<GuildOnboardingPrompt>().Remove(removed);
            auditLog.Log(guildId, actorUserId, AuditActionType.OnboardingPromptDeleted, removed.Id, new { removed.Title });
        }

        var now = DateTimeOffset.UtcNow;
        var result = new List<GuildOnboardingPrompt>();

        for (var index = 0; index < payload.Count; index++)
        {
            var dto = payload[index];
            var prompt = dto.Id is null ? null : existing.FirstOrDefault(p => p.Id == dto.Id);

            if (prompt is null)
            {
                prompt = new GuildOnboardingPrompt
                {
                    Id = GuildOnboardingPrompt.GenerateId(),
                    CreatedAt = now,
                    GuildId = guildId,
                };
                ctx.Set<GuildOnboardingPrompt>().Add(prompt);
                auditLog.Log(guildId, actorUserId, AuditActionType.OnboardingPromptCreated, prompt.Id, new { dto.Title });
            }
            else
            {
                auditLog.Log(guildId, actorUserId, AuditActionType.OnboardingPromptUpdated, prompt.Id, new { dto.Title });
            }

            prompt.UpdatedAt = now;
            prompt.Title = dto.Title;
            prompt.Type = dto.Type;
            prompt.SingleSelect = dto.SingleSelect;
            prompt.Required = dto.Required;
            prompt.InOnboarding = dto.InOnboarding;
            prompt.Position = index;

            ReconcileOptions(prompt, dto.Options, now);
            result.Add(prompt);
        }

        return result;
    }

    private void ReconcileOptions(GuildOnboardingPrompt prompt, List<OnboardingPromptOptionDto> payload, DateTimeOffset now)
    {
        var existing = prompt.Options.ToList();
        var keptIds = payload.Where(o => o.Id is not null).Select(o => o.Id!).ToHashSet();

        foreach (var removed in existing.Where(o => !keptIds.Contains(o.Id)))
            ctx.Set<GuildOnboardingPromptOption>().Remove(removed);

        var reconciled = new List<GuildOnboardingPromptOption>();

        for (var index = 0; index < payload.Count; index++)
        {
            var dto = payload[index];
            var option = dto.Id is null ? null : existing.FirstOrDefault(o => o.Id == dto.Id);

            if (option is null)
            {
                option = new GuildOnboardingPromptOption
                {
                    Id = GuildOnboardingPromptOption.GenerateId(),
                    CreatedAt = now,
                    PromptId = prompt.Id,
                };
                ctx.Set<GuildOnboardingPromptOption>().Add(option);
            }

            option.UpdatedAt = now;
            option.Title = dto.Title;
            option.Description = dto.Description;
            option.Emoji = dto.Emoji;
            option.RoleIds = dto.RoleIds.Distinct().ToList();
            option.ChannelIds = dto.ChannelIds.Distinct().ToList();
            option.Position = index;

            reconciled.Add(option);
        }

        prompt.Options = reconciled;
    }

    /// <summary>Marks every still-pending member of the guild as completed and drops their cached
    /// permissions. The cache is keyed per (guild, user) with no bulk invalidation, hence the loop.</summary>
    private async Task CompletePendingMembersAsync(string guildId)
    {
        var pending = await ctx.GuildMembers
            .Where(m => m.GuildId == guildId && m.OnboardingCompletedAt == null)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var member in pending)
        {
            member.OnboardingCompletedAt = now;
            await permissionService.InvalidateUserPermissionsCacheAsync(guildId, member.UserId);
        }
    }
}
