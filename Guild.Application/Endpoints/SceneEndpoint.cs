using System.Security.Claims;
using Echo.Realtime;
using FluentValidation;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// Scenes: a thread with a cast, a turn order and a clock. The turn moves on its own when the
/// character whose turn it is posts, so these routes exist for the cases play cannot express -
/// passing without posting, reordering, and skipping somebody who has gone quiet.
/// </summary>
[Authorize]
public class SceneEndpoint
{
    /// <summary>Opens a scene under a text channel, with its out-of-character companion thread.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/channels/{channelId}/scenes")]
    public async Task<IResult> CreateAsync(string guildId, string channelId, CreateSceneDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] AuditLogService auditLog, [NotBody] GuildHydrateService hydrate,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] IMessageBus bus,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var parent = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildId == guildId);
        if (parent is null) return Results.NotFound();

        // ManageScenes is guild-wide, so the channel the scene lands in is checked separately -
        // otherwise a GM could open one in a channel they cannot see.
        if (!await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.ViewChannel))
            return Results.Forbid();

        if (parent.Type != ChannelType.Text)
            return Results.BadRequest("A scene can only be opened under a Text channel.");

        var participants = (dto.ParticipantPersonaIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        foreach (var personaId in participants)
        {
            if (!await scenes.IsAdoptedAsync(guildId, personaId))
                return Results.BadRequest($"Persona '{personaId}' has not been adopted into this guild.");
        }

        var order = dto.TurnOrder ?? [];
        if (order.Except(participants, StringComparer.Ordinal).Any())
            return Results.BadRequest("The turn order can only name personas in the scene.");

        try
        {
            var scene = Channel.Create(new CreateChannelParams
            {
                Name = dto.Name,
                Description = dto.Description ?? "",
                Type = ChannelType.Scene,
                GuildId = guildId,
                ParentChannelId = channelId,
                CreatedByUserId = userId,
            });

            // Paired at creation rather than left to convention: every guide on running roleplay
            // says to keep the in-character and out-of-character rooms apart, and every server that
            // has to do it by hand ends up with half of them missing.
            var ooc = Channel.Create(new CreateChannelParams
            {
                Name = string.IsNullOrWhiteSpace(dto.OocName) ? $"{dto.Name} (OOC)" : dto.OocName,
                Description = "",
                Type = ChannelType.Thread,
                GuildId = guildId,
                ParentChannelId = channelId,
                CreatedByUserId = userId,
            });

            var state = SceneState.Create(new CreateSceneStateParams
            {
                ChannelId = scene.Id,
                GuildId = guildId,
                OocThreadId = ooc.Id,
                TurnLengthHours = dto.TurnLengthHours,
                ParticipantPersonaIds = participants,
            });

            state.TurnOrder = [.. order];

            ctx.Channels.Add(scene);
            ctx.Channels.Add(ooc);
            ctx.Set<SceneState>().Add(state);

            auditLog.Log(guildId, userId, AuditActionType.ChannelCreated, scene.Id,
                new { ParentChannelId = channelId, Type = nameof(ChannelType.Scene), OocThreadId = ooc.Id });

            // Both halves announce as threads, because both are: a scene the thread fan-out does not
            // carry is one that never reaches a bot's THREAD_CREATE.
            await AnnounceThreadAsync(hub, hydrate, bus, scene, channelId);
            await AnnounceThreadAsync(hub, hydrate, bus, ooc, channelId);

            return Results.Ok(SceneDto.From(state, scene));
        }
        catch (ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }
    }

    /// <summary>One scene, its cast and whose turn it is.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}")]
    public async Task<IResult> GetAsync(string guildId, string sceneChannelId,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Scenes))
            return Results.Forbid();

        if (!await permissionService.CanUserPerformActionAsync(userId, sceneChannelId, Permissions.ViewChannel))
            return Results.Forbid();

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        return Results.Ok(SceneDto.From(found.Value.State, found.Value.Channel));
    }

    /// <summary>Sets a scene's status, its clock or its turn order.</summary>
    [WolverinePatch("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}")]
    public async Task<IResult> UpdateAsync(string guildId, string sceneChannelId, UpdateSceneDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] AuditLogService auditLog, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        var (channel, state) = found.Value;
        var now = DateTimeOffset.UtcNow;

        if (dto.TurnOrder is not null)
        {
            if (dto.TurnOrder.Except(state.ParticipantPersonaIds, StringComparer.Ordinal).Any())
                return Results.BadRequest("The turn order can only name personas in the scene.");

            state.TurnOrder = [.. dto.TurnOrder.Distinct(StringComparer.Ordinal)];
        }

        if (dto.CurrentTurnPersonaId is not null)
        {
            if (!state.ParticipantPersonaIds.Contains(dto.CurrentTurnPersonaId, StringComparer.Ordinal))
                return Results.BadRequest("That persona is not in this scene.");

            state.CurrentTurnPersonaId = dto.CurrentTurnPersonaId;
            state.NudgeCount = 0;
            state.LastNudgedAt = null;
        }

        if (dto.TurnLengthHours.HasValue) state.TurnLengthHours = dto.TurnLengthHours.Value;
        if (dto.TurnDeadlineAt.HasValue) state.TurnDeadlineAt = dto.TurnDeadlineAt.Value;

        if (dto.Status is { } status) state.Status = status;

        // Starting a scene that nobody has the turn in is the one status change that has to do
        // something, or the first turn never begins and nothing is ever nudged.
        if (state.Status == SceneStatus.Active && state.CurrentTurnPersonaId is null)
        {
            var unavailable = await scenes.UnavailablePersonasAsync(guildId, state.Rotation, now);
            state.CurrentTurnPersonaId = state.NextTurn(null, unavailable);
            state.TurnDeadlineAt = state.CurrentTurnPersonaId is not null && state.TurnLengthHours is > 0
                ? now.AddHours(state.TurnLengthHours.Value)
                : state.TurnDeadlineAt;
        }

        state.UpdatedAt = now;

        auditLog.Log(guildId, userId, AuditActionType.ChannelUpdated, sceneChannelId,
            new { Scene = true, state.Status, state.CurrentTurnPersonaId });

        await scenes.BroadcastUpdatedAsync(state);

        return Results.Ok(SceneDto.From(state, channel));
    }

    /// <summary>Adds a character to the cast.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/participants")]
    public async Task<IResult> AddParticipantAsync(
        string guildId, string sceneChannelId, AddSceneParticipantDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        var (channel, state) = found.Value;

        if (!await scenes.IsAdoptedAsync(guildId, dto.PersonaId))
            return Results.BadRequest("That persona has not been adopted into this guild.");

        if (state.ParticipantPersonaIds.Contains(dto.PersonaId, StringComparer.Ordinal))
            return Results.Conflict("That persona is already in this scene.");

        state.ParticipantPersonaIds.Add(dto.PersonaId);

        if (state.TurnOrder.Count > 0)
        {
            var position = Math.Clamp(dto.Position ?? state.TurnOrder.Count, 0, state.TurnOrder.Count);
            state.TurnOrder.Insert(position, dto.PersonaId);
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;

        await scenes.BroadcastUpdatedAsync(state);

        return Results.Ok(SceneDto.From(state, channel));
    }

    /// <summary>Removes a character from the cast, handing the turn on when it was theirs.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/participants/{personaId}")]
    public async Task<IResult> RemoveParticipantAsync(
        string guildId, string sceneChannelId, string personaId,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        var (channel, state) = found.Value;
        var now = DateTimeOffset.UtcNow;
        var wasTheirTurn = string.Equals(state.CurrentTurnPersonaId, personaId, StringComparison.Ordinal);

        var unavailable = await scenes.UnavailablePersonasAsync(guildId, state.Rotation, now);
        if (!state.RemoveParticipant(personaId, unavailable, now)) return Results.NotFound();

        if (wasTheirTurn) await scenes.BroadcastTurnAsync(state, personaId);
        await scenes.BroadcastUpdatedAsync(state);

        return Results.Ok(SceneDto.From(state, channel));
    }

    /// <summary>
    /// Passes the turn on without posting. Open to whoever holds ManageScenes and to whoever
    /// answers for the character whose turn it is - passing is part of playing.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/turn/advance")]
    public async Task<IResult> AdvanceTurnAsync(string guildId, string sceneChannelId,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Scenes))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        var (channel, state) = found.Value;

        if (state.Status != SceneStatus.Active)
            return Results.BadRequest("The turn only moves while a scene is active.");

        var isGameMaster = await permissionService.CanUserPerformActionOnGuildAsync(
            userId, guildId, ModulePermissions.ManageScenes);

        if (!isGameMaster && !await AnswersForCurrentTurnAsync(scenes, state, userId))
            return Results.Forbid();

        await scenes.AdvanceAsync(state, DateTimeOffset.UtcNow);

        return Results.Ok(SceneDto.From(state, channel));
    }

    /// <summary>Skips a turn that has gone quiet. GM only - passing your own turn is
    /// <c>/turn/advance</c>.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/turn/skip")]
    public async Task<IResult> SkipTurnAsync(string guildId, string sceneChannelId,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        var (channel, state) = found.Value;

        if (state.Status != SceneStatus.Active)
            return Results.BadRequest("The turn only moves while a scene is active.");

        await scenes.AdvanceAsync(state, DateTimeOffset.UtcNow);

        return Results.Ok(SceneDto.From(state, channel));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static Task<IResult?> Gate(
        GuildPermissionService permissions, MicroserviceContext ctx, string guildId, string userId) =>
        PersonaGate.CheckAsync(permissions, ctx, guildId, userId,
            ModulePermissions.ManageScenes, GuildFeatures.Scenes);

    /// <summary>The scene channel and its turn state, or null when the id is not a scene of this
    /// guild.</summary>
    private static async Task<(Channel Channel, SceneState State)?> ResolveAsync(
        MicroserviceContext ctx, SceneService scenes, string guildId, string sceneChannelId)
    {
        var channel = await ctx.Channels.FirstOrDefaultAsync(
            c => c.Id == sceneChannelId && c.GuildId == guildId && c.Type == ChannelType.Scene);

        if (channel is null) return null;

        var state = await scenes.GetAsync(sceneChannelId);
        return state is null ? null : (channel, state);
    }

    private static async Task<bool> AnswersForCurrentTurnAsync(
        SceneService scenes, SceneState state, string userId)
    {
        if (state.CurrentTurnPersonaId is null) return false;

        var owners = await scenes.OwnersByPersonaAsync(state.GuildId, [state.CurrentTurnPersonaId]);
        return owners.TryGetValue(state.CurrentTurnPersonaId, out var players)
               && players.Contains(userId, StringComparer.Ordinal);
    }

    /// <summary>Announces a newly created thread-shaped channel to clients and to bots.</summary>
    private static async Task AnnounceThreadAsync(
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate, IMessageBus bus,
        Channel channel, string parentChannelId)
    {
        var presence = await hydrate.GetGuildPresenceAsync(channel.GuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ThreadCreated", new
        {
            ChannelId = channel.Id,
            ParentChannelId = parentChannelId,
            GuildId = channel.GuildId,
            TagIds = Array.Empty<string>(),
        });

        await bus.PublishAsync(new ThreadCreatedForBots
        {
            ChannelId = channel.Id,
            GuildId = channel.GuildId,
            ParentChannelId = parentChannelId,
            Name = channel.Name,
        });
    }
}
