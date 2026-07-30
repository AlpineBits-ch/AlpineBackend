using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Persistence;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints;

/// <summary>
/// The venta client's side of message components - the counterpart to
/// BotCommandEndpoint.InvokeCommandAsync, and deliberately shaped the same way.
/// </summary>
[Authorize]
public class ComponentInteractionEndpoint
{
    [WolverinePost("/api/v1/guilds/{guildId}/channels/{channelId}/messages/{messageId}/component-interactions")]
    public async Task<IResult> InvokeComponentAsync(string guildId, string channelId, string messageId,
        InvokeComponentDto dto, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] IMessageBus bus, [NotBody] GatewayConnectionRegistry registry,
        [NotBody] PendingInteractionStore pendingStore)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.CustomId)) return Results.BadRequest("customId is required");

        // Pressing a button is participation in the channel, so it takes the same permission as
        // speaking there - otherwise a muted or read-only member could still drive a bot's flow.
        var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(new HasUserPermissionToChannelRequest
        {
            ChannelId = channelId,
            UserId = userId,
            Permission = ExternalPermission.SendMessages,
        });
        if (!permission.IsAllowed) return Results.Forbid();

        // An ephemeral message exists only in Redis, so it is resolved separately - and only ever
        // for the user it was sent to.
        if (messageId.StartsWith("ephm_", StringComparison.Ordinal))
        {
            var ephemeral = await pendingStore.GetEphemeralAsync(messageId);
            if (ephemeral is null || ephemeral.InvokingUserId != userId) return Results.NotFound();
            if (!ephemeral.CustomIds.Contains(dto.CustomId)) return Results.BadRequest("Unknown customId for this message.");

            return await DispatchAsync(ephemeral.BotUserId, guildId, channelId, messageId, userId, dto,
                originalContent: string.Empty, originalComponents: [], ctx, bus, registry, pendingStore);
        }

        var message = await bus.InvokeAsync<GetMessageResponse>(new GetMessageRequest { MessageId = messageId });
        if (message.Message is null || message.Message.ChannelId != channelId) return Results.NotFound();

        var components = string.IsNullOrWhiteSpace(message.Message.ComponentsJson)
            ? []
            : JsonSerializer.Deserialize<List<ComponentPayload>>(message.Message.ComponentsJson) ?? [];

        // Without this, any client could invent a custom_id and drive a bot into a code path no
        // button on screen would have reached.
        var known = components.SelectMany(c => c.CollectCustomIds()).ToHashSet();
        if (!known.Contains(dto.CustomId)) return Results.BadRequest("Unknown customId for this message.");

        return await DispatchAsync(message.Message.AuthorId, guildId, channelId, messageId, userId, dto,
            Encoding.UTF8.GetString(message.Message.Content), components, ctx, bus, registry, pendingStore);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/channels/{channelId}/modal-submit")]
    public async Task<IResult> SubmitModalAsync(string guildId, string channelId, SubmitModalDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus,
        [NotBody] GatewayConnectionRegistry registry, [NotBody] PendingInteractionStore pendingStore)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.CustomId)) return Results.BadRequest("customId is required");

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

        var (interactionId, token) = NewInteractionIdentity();

        var interaction = await BuildInteractionAsync(interactionId, token, app.BotUserId, guildId, channelId, userId, bus);
        interaction.Type = InteractionType.ModalSubmit;
        interaction.Data = new InteractionDataPayload
        {
            CustomId = dto.CustomId,
            Components = dto.Components,
        };

        await pendingStore.SaveAsync(token, new PendingInteraction(
            interactionId, app.BotUserId, guildId, channelId, userId, dto.CustomId, Acknowledged: false,
            MessageId: null, InteractionType: InteractionType.ModalSubmit));

        await registry.PublishAsync(app.BotUserId, "INTERACTION_CREATE", interaction);

        return Results.Accepted();
    }

    private static async Task<IResult> DispatchAsync(string botUserId, string guildId, string channelId,
        string messageId, string userId, InvokeComponentDto dto, string originalContent,
        List<ComponentPayload> originalComponents, MicroserviceContext ctx, IMessageBus bus,
        GatewayConnectionRegistry registry, PendingInteractionStore pendingStore)
    {
        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.BotUserId == botUserId && a.IsEnabled);
        if (app is null) return Results.NotFound("The bot that owns this message is not available.");

        var installed = await ctx.BotInstallations.AnyAsync(i => i.BotApplicationId == app.Id && i.GuildId == guildId);
        if (!installed) return Results.NotFound("Bot is not installed in this guild.");

        var (interactionId, token) = NewInteractionIdentity();

        var interaction = await BuildInteractionAsync(interactionId, token, app.BotUserId, guildId, channelId, userId, bus);
        interaction.Type = InteractionType.MessageComponent;
        interaction.Data = new InteractionDataPayload
        {
            CustomId = dto.CustomId,
            ComponentType = dto.ComponentType,
            Values = dto.Values,
        };
        interaction.Message = new InteractionMessagePayload
        {
            Id = messageId,
            ChannelId = channelId,
            Content = originalContent,
            Components = originalComponents,
        };

        await pendingStore.SaveAsync(token, new PendingInteraction(
            interactionId, app.BotUserId, guildId, channelId, userId, dto.CustomId, Acknowledged: false,
            MessageId: messageId, InteractionType: InteractionType.MessageComponent));

        await registry.PublishAsync(app.BotUserId, "INTERACTION_CREATE", interaction);

        return Results.Accepted();
    }

    /// <summary>
    /// Autocomplete: the client is holding a dropdown open waiting for choices, so unlike every
    /// other interaction this one needs an answer now.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/channels/{channelId}/autocomplete")]
    public async Task<IResult> AutocompleteAsync(string guildId, string channelId, AutocompleteRequestDto dto,
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

        var command = await ctx.BotCommands.FirstOrDefaultAsync(c =>
            c.BotApplicationId == app.Id && c.Name == dto.CommandName && (c.GuildId == null || c.GuildId == guildId));
        if (command is null) return Results.NotFound("Command not found.");

        var (interactionId, token) = NewInteractionIdentity();

        var interaction = await BuildInteractionAsync(interactionId, token, app.BotUserId, guildId, channelId, userId, bus);
        interaction.Type = InteractionType.Autocomplete;
        interaction.Data = new InteractionDataPayload
        {
            Id = command.Id,
            Name = command.Name,
            Type = 1,
            Options = dto.Options.Select(o => new InteractionOptionPayload
            {
                Name = o.Name,
                Type = o.Type,
                // An omitted value deserializes to default(JsonElement), whose ValueKind is
                // Undefined - which System.Text.Json refuses to write.
                Value = o.Value?.ValueKind is null or JsonValueKind.Undefined ? EmptyStringElement : o.Value.Value,
            }).ToList(),
        };

        await pendingStore.SaveAsync(token, new PendingInteraction(
            interactionId, app.BotUserId, guildId, channelId, userId, command.Name, Acknowledged: false,
            MessageId: null, InteractionType: InteractionType.Autocomplete));

        await registry.PublishAsync(app.BotUserId, "INTERACTION_CREATE", interaction);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(AutocompleteTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stored = await pendingStore.GetAutocompleteResultAsync(interactionId);
            if (stored is not null) return Results.Content(stored, "application/json");

            await Task.Delay(AutocompletePollMilliseconds);
        }

        // An empty list is the honest answer to "the bot did not respond in time" - the dropdown
        // shows no suggestions rather than an error the user can do nothing about.
        return Results.Ok(Array.Empty<AutocompleteChoicePayload>());
    }

    /// <summary>Deliberately short.</summary>
    private const int AutocompleteTimeoutSeconds = 3;

    private const int AutocompletePollMilliseconds = 50;

    /// <summary>A writable stand-in for an absent option value.</summary>
    private static readonly JsonElement EmptyStringElement = JsonDocument.Parse("\"\"").RootElement.Clone();

    private static (string InteractionId, string Token) NewInteractionIdentity() =>
        (Ksuid.Generate("intr_"), Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

    /// <summary>The fields every interaction type shares.</summary>
    private static async Task<InteractionPayload> BuildInteractionAsync(string interactionId, string token,
        string botUserId, string guildId, string channelId, string userId, IMessageBus bus)
    {
        var memberResponse = await bus.InvokeAsync<GetGuildMemberResponse>(
            new GetGuildMemberRequest { GuildId = guildId, UserId = userId });

        return new InteractionPayload
        {
            Id = interactionId,
            ApplicationId = botUserId,
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
    }
}

public class InvokeComponentDto
{
    public string CustomId { get; set; } = "";

    /// <summary>The pressed component's type - 2 for a button, 3+ for the selects.</summary>
    public int ComponentType { get; set; } = Bots.Contracts.Gateway.Payloads.ComponentType.Button;

    /// <summary>Selected values for a select menu; empty for a button.</summary>
    public List<string> Values { get; set; } = [];
}

public class AutocompleteRequestDto
{
    public string BotUserId { get; set; } = "";
    public string CommandName { get; set; } = "";

    /// <summary>The options as typed so far, including the partially-filled one the user is
    /// currently editing - the bot needs the whole set to suggest sensibly.</summary>
    public List<AutocompleteOptionDto> Options { get; set; } = [];
}

public class AutocompleteOptionDto
{
    public string Name { get; set; } = "";

    /// <summary>Discord option type; 3 (STRING) covers virtually every autocompleted option.</summary>
    public int Type { get; set; } = 3;

    /// <summary>Nullable so an option the user has not typed into yet round-trips - see the
    /// substitution where this is mapped.</summary>
    public JsonElement? Value { get; set; }
}

public class SubmitModalDto
{
    public string BotUserId { get; set; } = "";
    public string CustomId { get; set; } = "";

    /// <summary>The submitted rows, in Discord's shape: action rows wrapping filled text inputs.</summary>
    public List<ComponentPayload> Components { get; set; } = [];
}
