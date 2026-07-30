using Guild.Application.Dtos.Request;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>Applies an imported Discord guild's onboarding and welcome screen.</summary>
public class ImportGuildOnboardingHandler
{
    public static async Task<ImportGuildOnboardingResponse> Handle(ImportGuildOnboardingRequest request,
        MicroserviceContext ctx, OnboardingConfigService configService)
    {
        var result = new ImportGuildOnboardingResponse();

        if (request.Onboarding is not null)
        {
            var dto = new UpdateOnboardingConfigDto
            {
                Enabled = request.Onboarding.Enabled,
                RulesText = request.Onboarding.RulesText,
                Mode = (OnboardingMode)request.Onboarding.Mode,
                DefaultChannelIds = request.Onboarding.DefaultChannelIds,
                Prompts = request.Onboarding.Prompts.Select(p => new OnboardingPromptDto
                {
                    Title = Truncate(p.Title, OnboardingValidationService.MaxTitleLength),
                    Type = (OnboardingPromptType)p.Type,
                    SingleSelect = p.SingleSelect,
                    Required = p.Required,
                    InOnboarding = p.InOnboarding,
                    Position = p.Position,
                    Options = p.Options.Select(o => new OnboardingPromptOptionDto
                    {
                        Title = Truncate(o.Title, OnboardingValidationService.MaxTitleLength),
                        Description = Truncate(o.Description, OnboardingValidationService.MaxDescriptionLength),
                        Emoji = o.Emoji,
                        RoleIds = o.RoleIds,
                        ChannelIds = o.ChannelIds,
                        Position = o.Position,
                    }).ToList(),
                }).ToList(),
            };

            // Discord allows an option that grants nothing; we don't.
            foreach (var prompt in dto.Prompts)
                prompt.Options.RemoveAll(o => o.RoleIds.Count + o.ChannelIds.Count == 0);
            dto.Prompts.RemoveAll(p => p.Options.Count == 0);

            var (error, _) = await configService.ReplaceAsync(request.GuildId, request.ActorUserId, dto);
            if (error is not null) return new ImportGuildOnboardingResponse { ErrorMessage = error };

            result.OnboardingApplied = true;
        }

        if (request.WelcomeScreen is not null)
        {
            await ApplyWelcomeScreenAsync(request, ctx);
            result.WelcomeScreenApplied = true;
        }

        return result;
    }

    private static async Task ApplyWelcomeScreenAsync(ImportGuildOnboardingRequest request, MicroserviceContext ctx)
    {
        var channelIds = request.WelcomeScreen!.Channels.Select(c => c.ChannelId).ToList();
        var knownChannelIds = await ctx.Channels.AsNoTracking()
            .Where(c => c.GuildId == request.GuildId && channelIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToHashSetAsync();

        var now = DateTimeOffset.UtcNow;

        var screen = new GuildWelcomeScreen
        {
            GuildId = request.GuildId,
            // Discord's welcome screen has no enabled flag on the payload - a guild that has one
            // has it on, so importing it turns ours on too.
            Enabled = true,
            Description = Truncate(request.WelcomeScreen.Description, GuildWelcomeScreen.MaxDescriptionLength),
            UpdatedAt = now,
            Channels = request.WelcomeScreen.Channels
                .Where(c => knownChannelIds.Contains(c.ChannelId))
                .Take(GuildWelcomeScreen.MaxChannels)
                .Select((c, index) => new GuildWelcomeChannel
                {
                    Id = GuildWelcomeChannel.GenerateId(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    GuildId = request.GuildId,
                    ChannelId = c.ChannelId,
                    Description = Truncate(c.Description, GuildWelcomeScreen.MaxChannelDescriptionLength)!,
                    Emoji = c.Emoji,
                    Position = index,
                })
                .ToList(),
        };

        ctx.Set<GuildWelcomeScreen>().Add(screen);
    }

    /// <summary>Discord's limits are not ours - truncate rather than reject, since the user is
    /// importing someone else's server and can edit it afterwards.</summary>
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
