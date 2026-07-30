using System.Security.Claims;
using System.Text.Json.Serialization;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

/// <summary>
/// Discord-compatible server onboarding: GET/PUT /guilds/{guild.id}/onboarding in Discord's
/// snake_case wire shape, so a minimally-modified Discord bot library can drive it unchanged.
/// Requires the bot to hold ManageGuild, matching Discord's MANAGE_GUILD + MANAGE_ROLES
/// requirement (role hierarchy is enforced per-role inside the guild service).
/// </summary>
[Authorize]
public class DiscordGuildOnboardingEndpoint
{
    [WolverineGet("/api/discord/v10/guilds/{guildId}/onboarding")]
    public async Task<IResult> GetAsync(string guildId, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var response = await bus.InvokeAsync<GetGuildOnboardingResponse>(new GetGuildOnboardingRequest
        {
            GuildId = guildId,
            ActorUserId = botUserId,
        });

        if (!response.Authorized) return Results.Forbid();

        return Results.Ok(ToDiscord(guildId, response.Config!));
    }

    [WolverinePut("/api/discord/v10/guilds/{guildId}/onboarding")]
    public async Task<IResult> UpdateAsync(string guildId, DiscordOnboardingPayload payload,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var botUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(botUserId)) return Results.Unauthorized();

        var response = await bus.InvokeAsync<UpdateGuildOnboardingResponse>(new UpdateGuildOnboardingRequest
        {
            GuildId = guildId,
            ActorUserId = botUserId,
            Config = FromDiscord(payload),
        });

        if (!response.Authorized) return Results.Forbid();
        if (response.Error is not null) return Results.BadRequest(new { message = response.Error, code = 50035 });

        return Results.Ok(ToDiscord(guildId, response.Config!));
    }

    private static object ToDiscord(string guildId, GuildOnboardingContract config) => new
    {
        guild_id = guildId,
        enabled = config.Enabled,
        mode = config.Mode,
        default_channel_ids = config.DefaultChannelIds,
        prompts = config.Prompts.Select(p => new
        {
            id = p.Id,
            title = p.Title,
            type = p.Type,
            single_select = p.SingleSelect,
            required = p.Required,
            in_onboarding = p.InOnboarding,
            options = p.Options.Select(o => new
            {
                id = o.Id,
                title = o.Title,
                description = o.Description,
                emoji = o.Emoji is null ? null : new { name = o.Emoji },
                role_ids = o.RoleIds,
                channel_ids = o.ChannelIds,
            }),
        }),
    };

    private static GuildOnboardingContract FromDiscord(DiscordOnboardingPayload payload) => new()
    {
        Enabled = payload.Enabled,
        // Discord has no rules-text field on this endpoint (that lives on member-verification), so
        // a bot PUT never changes it - the guild service keeps whatever is stored.
        RulesText = payload.RulesText,
        Mode = payload.Mode,
        DefaultChannelIds = payload.DefaultChannelIds ?? [],
        Prompts = (payload.Prompts ?? []).Select((p, index) => new GuildOnboardingPromptContract
        {
            Id = p.Id,
            Title = p.Title,
            Type = p.Type,
            SingleSelect = p.SingleSelect,
            Required = p.Required,
            InOnboarding = p.InOnboarding,
            Position = index,
            Options = (p.Options ?? []).Select((o, optionIndex) => new GuildOnboardingOptionContract
            {
                Id = o.Id,
                Title = o.Title,
                Description = o.Description,
                Emoji = o.Emoji?.Name,
                RoleIds = o.RoleIds ?? [],
                ChannelIds = o.ChannelIds ?? [],
                Position = optionIndex,
            }).ToList(),
        }).ToList(),
    };
}

public class DiscordOnboardingPayload
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("mode")] public int Mode { get; set; }
    [JsonPropertyName("default_channel_ids")] public List<string>? DefaultChannelIds { get; set; }
    [JsonPropertyName("prompts")] public List<DiscordOnboardingPrompt>? Prompts { get; set; }

    /// <summary>Not part of Discord's payload - accepted so a bot can set the rules screen too
    /// rather than needing a second, non-Discord call.</summary>
    [JsonPropertyName("rules_text")] public string? RulesText { get; set; }
}

public class DiscordOnboardingPrompt
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = null!;
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("single_select")] public bool SingleSelect { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("in_onboarding")] public bool InOnboarding { get; set; } = true;
    [JsonPropertyName("options")] public List<DiscordOnboardingOption>? Options { get; set; }
}

public class DiscordOnboardingOption
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = null!;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("emoji")] public DiscordOnboardingEmoji? Emoji { get; set; }
    [JsonPropertyName("role_ids")] public List<string>? RoleIds { get; set; }
    [JsonPropertyName("channel_ids")] public List<string>? ChannelIds { get; set; }
}

public class DiscordOnboardingEmoji
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}
