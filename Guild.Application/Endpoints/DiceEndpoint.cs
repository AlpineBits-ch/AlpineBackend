using System.Security.Claims;
using System.Text;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;
using MessagingAuthorIdType = Messaging.Contracts.Bus.Commands.AuthorIdType;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Guild.Application.Endpoints;

/// <summary>
/// Server-rolled dice. The roll happens here rather than in Messaging because the module
/// permission, the persona and the channel all resolve in Guild, and the result is a message
/// created through the same <c>CreateMessageCommand</c> path the rest of this service uses.
/// </summary>
[Authorize]
public class DiceEndpoint
{
    /// <summary>
    /// Rolls an expression into a channel and records the result.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="channelId">The channel the roll is posted into.</param>
    /// <param name="dto">The expression, the character rolling it, and what it is for.</param>
    /// <param name="permissionService">The permission resolver.</param>
    /// <param name="personas">Persona resolution, shared with the send path.</param>
    /// <param name="roller">Where the faces come from.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="bus">The bus the message is created over.</param>
    /// <param name="user">The caller.</param>
    /// <returns>The recorded roll, or the reason it was refused.</returns>
    [WolverinePost("/api/v1/guilds/{guildId}/channels/{channelId}/rolls")]
    public async Task<IResult> RollAsync(string guildId, string channelId, CreateDiceRollDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] IDieRoller roller, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckAsync(
                permissionService, ctx, guildId, userId, ModulePermissions.RollDice, GuildFeatures.Dice) is { } denied)
            return denied;

        // A hidden roll is refused rather than accepted and downgraded to public: honouring a
        // privacy request in the weakest possible way is worse than saying no to it.
        if (dto.Visibility != DiceVisibility.Public)
            return Results.BadRequest("Only public rolls are supported. A hidden or blind roll is not available yet.");

        if (!await ctx.Channels.AnyAsync(c => c.Id == channelId && c.GuildId == guildId))
            return Results.NotFound();

        // A roll is a post, so it needs the same permission any other message in this channel does.
        if (!await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.SendMessages))
            return Results.Forbid();

        var reason = dto.Reason?.Trim();
        if (reason?.Length > DiceRollRenderer.MaxReasonLength)
            return Results.BadRequest($"A reason cannot exceed {DiceRollRenderer.MaxReasonLength} characters.");

        var parsed = DiceNotationParser.Parse(dto.Expression);
        if (!parsed.Ok) return Results.BadRequest(parsed.Error);

        var persona = await ResolvePersonaAsync(permissionService, personas, ctx, guildId, channelId, userId, dto.PersonaId);
        if (persona.Error is not null)
        {
            return Results.Json(
                new { error = "persona_not_usable", message = persona.Error },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var outcome = DiceEvaluator.Evaluate(parsed.Terms, parsed.Normalized, roller);
        var content = DiceRollRenderer.Content(outcome, string.IsNullOrWhiteSpace(reason) ? null : reason);

        var message = await bus.InvokeAsync<Message>(new CreateMessageCommand
        {
            Content = Encoding.UTF8.GetBytes(content),
            ChannelId = channelId,
            // The real account, always. A persona only ever changes the display overrides.
            AuthorId = userId,
            AuthorIdType = persona.PersonaId is null ? MessagingAuthorIdType.User : MessagingAuthorIdType.Persona,
            AuthorDisplayName = persona.DisplayName,
            AuthorAvatarUrl = persona.AvatarUrl,
            PersonaId = persona.PersonaId,
            Type = MessagingMessageType.DiceRoll,
            EmbedsJson = DiceRollRenderer.EmbedsJson(outcome, string.IsNullOrWhiteSpace(reason) ? null : reason),
            Mentions = [],
        });

        var roll = DiceRoll.Create(new CreateDiceRollParams
        {
            MessageId = message.Id,
            GuildId = guildId,
            ChannelId = channelId,
            RollerUserId = userId,
            PersonaId = persona.PersonaId,
            Expression = outcome.Expression,
            ResultsJson = DiceRollRenderer.ResultsJson(outcome),
            Total = outcome.Total,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            Visibility = DiceVisibility.Public,
        });

        ctx.Set<DiceRoll>().Add(roll);

        return Results.Ok(ToDto(roll, outcome));
    }

    private readonly record struct RolledAs(string? PersonaId, string? DisplayName, string? AvatarUrl, string? Error);

    /// <summary>
    /// Resolves the character the roll goes out as, exactly the way the send path does. A guild
    /// with dice but no personas simply rolls as the user, unless the client named one, which is a
    /// request that has to fail rather than be dropped.
    /// </summary>
    private static async Task<RolledAs> ResolvePersonaAsync(
        GuildPermissionService permissionService, PersonaService personas, MicroserviceContext ctx,
        string guildId, string channelId, string userId, string? personaId)
    {
        var personaGate = await PersonaGate.CheckAsync(
            permissionService, ctx, guildId, userId, ModulePermissions.UsePersonas);

        if (personaGate is not null)
        {
            return string.IsNullOrWhiteSpace(personaId)
                ? new RolledAs(null, null, null, null)
                : new RolledAs(null, null, null, "You cannot speak as a persona here.");
        }

        // Content is null on purpose: a dice expression is notation, not prose, so there is no
        // proxy prefix to match in it. An explicit id and the channel's autoproxy still apply.
        var resolution = await personas.ResolveForSendAsync(new PersonaSendContext
        {
            UserId = userId,
            GuildId = guildId,
            ChannelId = channelId,
            PersonaId = personaId,
            Content = null,
        });

        return resolution.Outcome switch
        {
            PersonaResolutionOutcome.Forbidden => new RolledAs(null, null, null, resolution.Error ?? "You cannot speak as that persona here."),
            PersonaResolutionOutcome.Resolved => new RolledAs(resolution.PersonaId, resolution.DisplayName, resolution.AvatarUrl, null),
            _ => new RolledAs(null, null, null, null),
        };
    }

    private static DiceRollDto ToDto(DiceRoll roll, DiceRollOutcome outcome) => new()
    {
        Id = roll.Id,
        MessageId = roll.MessageId,
        ChannelId = roll.ChannelId,
        RollerUserId = roll.RollerUserId,
        PersonaId = roll.PersonaId,
        Expression = roll.Expression,
        Reason = roll.Reason,
        Total = roll.Total,
        Visibility = roll.Visibility,
        Breakdown = outcome.Breakdown,
        Terms = outcome.Terms.Select(DiceRollTermDto.From).ToList(),
        CreatedAt = roll.CreatedAt,
    };
}
