using System.Security.Claims;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Persistence;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints;

public class InvokeCommandOptionDto
{
    public string Name { get; set; } = "";
    public JsonElement Value { get; set; }
}

public class InvokeCommandDto
{
    public string BotUserId { get; set; } = "";
    public string CommandName { get; set; } = "";
    public List<InvokeCommandOptionDto> Options { get; set; } = new();
}

/// <summary>
/// Native (venta JWT-authed, not Discord-compat) surface for slash commands: there's no separate
/// "real Discord client" reading a command registry and building an Interaction locally - venta's
/// own client calls straight into this instead. Discovery lets a future client "/" picker know
/// what's available; invoking builds a Discord-shaped Interaction and dispatches it exactly like
/// a real Discord backend would, over the same Gateway fan-out MESSAGE_CREATE already uses.
/// </summary>
[Authorize]
public class BotCommandEndpoint
{
    /// <summary>
    /// Requires the caller to be able to see the guild. Previously this read no identity at all,
    /// so any authenticated user could list every bot installed in any guild - which also made it
    /// the cheapest way to harvest botUserId values.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/commands")]
    public async Task<IResult> GetCommandsForGuildAsync(string guildId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var permission = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(new HasUserPermissionToGuildRequest
        {
            GuildId = guildId,
            UserId = userId,
            Permission = ExternalPermission.ViewChannel,
        });
        if (!permission.IsAllowed) return Results.Forbid();

        var appIds = await ctx.BotInstallations.AsNoTracking()
            .Where(i => i.GuildId == guildId)
            .Select(i => i.BotApplicationId)
            .ToListAsync();

        if (appIds.Count == 0) return Results.Ok(Array.Empty<object>());

        var commands = await ctx.BotCommands.AsNoTracking()
            .Where(c => appIds.Contains(c.BotApplicationId) && (c.GuildId == null || c.GuildId == guildId))
            .Join(ctx.BotApplications.AsNoTracking(), c => c.BotApplicationId, a => a.Id, (c, a) => new { c, a })
            .ToListAsync();

        return Results.Ok(commands.Select(x => new
        {
            botUserId = x.a.BotUserId,
            botName = x.a.Name,
            name = x.c.Name,
            description = x.c.Description,
            options = JsonSerializer.Deserialize<JsonElement>(x.c.OptionsJson),
            scope = x.c.GuildId is null ? "global" : "guild",
        }));
    }

    [WolverinePost("/api/v1/guilds/{guildId}/channels/{channelId}/interactions")]
    public async Task<IResult> InvokeCommandAsync(string guildId, string channelId, InvokeCommandDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus,
        [NotBody] GatewayConnectionRegistry registry, [NotBody] PendingInteractionStore pendingStore)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(new HasUserPermissionToChannelRequest
        {
            ChannelId = channelId,
            UserId = userId,
            Permission = ExternalPermission.SendMessages,
        });
        if (!permission.IsAllowed) return Results.Forbid();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.BotUserId == dto.BotUserId && a.IsEnabled);
        if (app is null) return Results.NotFound("Bot not found.");

        var installed = await ctx.BotInstallations.AnyAsync(i => i.BotApplicationId == app.Id && i.GuildId == guildId);
        if (!installed) return Results.NotFound("Bot is not installed in this guild.");

        var command = await ctx.BotCommands.FirstOrDefaultAsync(c =>
            c.BotApplicationId == app.Id && c.Name == dto.CommandName && (c.GuildId == null || c.GuildId == guildId));
        if (command is null) return Results.NotFound("Command not found.");

        var optionTypesByName = ParseOptionTypes(command.OptionsJson);

        var memberResponse = await bus.InvokeAsync<GetGuildMemberResponse>(
            new GetGuildMemberRequest { GuildId = guildId, UserId = userId });

        var interactionId = Ksuid.Generate("intr_");
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        var interaction = new InteractionPayload
        {
            Id = interactionId,
            ApplicationId = app.BotUserId,
            Type = 2,
            Data = new InteractionDataPayload
            {
                Id = command.Id,
                Name = command.Name,
                Type = 1,
                Options = dto.Options.Select(o => new InteractionOptionPayload
                {
                    Name = o.Name,
                    Type = optionTypesByName.GetValueOrDefault(o.Name, 3),
                    Value = o.Value,
                }).ToList(),
            },
            GuildId = guildId,
            Channel = new InteractionChannelPayload { Id = channelId, Type = DiscordChannelType.GuildText },
            ChannelId = channelId,
            Member = memberResponse.Member is null ? null : new GatewayMemberPayload
            {
                User = new DiscordUserPayload { Id = userId, Username = userId, Bot = false },
                Nick = memberResponse.Member.Nickname,
                Roles = memberResponse.Member.RoleIds,
                JoinedAt = memberResponse.Member.JoinedAt,
            },
            Token = token,
            Version = 1,
        };

        await pendingStore.SaveAsync(token,
            new PendingInteraction(interactionId, app.BotUserId, guildId, channelId, userId, command.Name, Acknowledged: false));

        await registry.PublishAsync(app.BotUserId, "INTERACTION_CREATE", interaction);

        return Results.Accepted();
    }

    /// <summary>Reads each option's declared Discord type out of the command's stored schema, so
    /// invocation values carry the same option type a real Discord interaction would - falls
    /// back to STRING (3) for anything not found rather than failing the invocation.</summary>
    private static Dictionary<string, int> ParseOptionTypes(string optionsJson)
    {
        var result = new Dictionary<string, int>();
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            foreach (var option in doc.RootElement.EnumerateArray())
            {
                if (option.TryGetProperty("name", out var name) && option.TryGetProperty("type", out var type))
                {
                    result[name.GetString()!] = type.GetInt32();
                }
            }
        }
        catch (JsonException)
        {
            // malformed/empty schema - every option just falls back to STRING
        }

        return result;
    }
}
