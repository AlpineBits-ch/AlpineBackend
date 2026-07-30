using Guild.Application.Dtos.Request;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;

namespace Guild.Application.Bus.Consumers;

/// <summary>Backs the Discord-compatible onboarding surface in Bots.Application. Everything routes
/// through OnboardingConfigService, so a bot gets exactly the same validation - including the
/// privileged-role guard - as the first-party endpoint.</summary>
public class GetGuildOnboardingHandler
{
    public static async Task<GetGuildOnboardingResponse> Handle(GetGuildOnboardingRequest request,
        GuildPermissionService permissionService, OnboardingConfigService configService)
    {
        if (!await permissionService.CanUserPerformActionOnGuildAsync(request.ActorUserId, request.GuildId, Permissions.ManageGuild))
            return new GetGuildOnboardingResponse { Authorized = false };

        var config = await configService.GetAsync(request.GuildId);

        return new GetGuildOnboardingResponse { Authorized = true, Config = ToContract(config) };
    }

    internal static GuildOnboardingContract ToContract(UpdateOnboardingConfigDto config) => new()
    {
        Enabled = config.Enabled,
        RulesText = config.RulesText,
        Mode = (int)config.Mode,
        DefaultChannelIds = config.DefaultChannelIds,
        Prompts = config.Prompts.Select(p => new GuildOnboardingPromptContract
        {
            Id = p.Id,
            Title = p.Title,
            Type = (int)p.Type,
            SingleSelect = p.SingleSelect,
            Required = p.Required,
            InOnboarding = p.InOnboarding,
            Position = p.Position,
            Options = p.Options.Select(o => new GuildOnboardingOptionContract
            {
                Id = o.Id,
                Title = o.Title,
                Description = o.Description,
                Emoji = o.Emoji,
                RoleIds = o.RoleIds,
                ChannelIds = o.ChannelIds,
                Position = o.Position,
            }).ToList(),
        }).ToList(),
    };
}

public class UpdateGuildOnboardingHandler
{
    public static async Task<UpdateGuildOnboardingResponse> Handle(UpdateGuildOnboardingRequest request,
        GuildPermissionService permissionService, OnboardingConfigService configService)
    {
        if (!await permissionService.CanUserPerformActionOnGuildAsync(request.ActorUserId, request.GuildId, Permissions.ManageGuild))
            return new UpdateGuildOnboardingResponse { Authorized = false };

        var (error, config) = await configService.ReplaceAsync(request.GuildId, request.ActorUserId,
            FromContract(request.Config));

        return new UpdateGuildOnboardingResponse
        {
            Authorized = true,
            Error = error,
            Config = config is null ? null : GetGuildOnboardingHandler.ToContract(config),
        };
    }

    private static UpdateOnboardingConfigDto FromContract(GuildOnboardingContract contract) => new()
    {
        Enabled = contract.Enabled,
        RulesText = contract.RulesText,
        Mode = (OnboardingMode)contract.Mode,
        DefaultChannelIds = contract.DefaultChannelIds,
        Prompts = contract.Prompts.Select(p => new OnboardingPromptDto
        {
            Id = p.Id,
            Title = p.Title,
            Type = (OnboardingPromptType)p.Type,
            SingleSelect = p.SingleSelect,
            Required = p.Required,
            InOnboarding = p.InOnboarding,
            Position = p.Position,
            Options = p.Options.Select(o => new OnboardingPromptOptionDto
            {
                Id = o.Id,
                Title = o.Title,
                Description = o.Description,
                Emoji = o.Emoji,
                RoleIds = o.RoleIds,
                ChannelIds = o.ChannelIds,
                Position = o.Position,
            }).ToList(),
        }).ToList(),
    };
}
