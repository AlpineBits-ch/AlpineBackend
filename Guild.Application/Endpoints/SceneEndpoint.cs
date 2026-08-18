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
    /// <summary>Scenes per page of the list route.</summary>
    public const int DefaultListSize = 50;

    public const int MaxListSize = 200;

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
            return Fault("scene_parent_not_text", "A scene can only be opened under a text channel.");

        if (dto.Status is { } wanted and not (SceneStatus.Open or SceneStatus.Active))
            return Fault("scene_status_not_openable", $"A scene cannot be created {wanted}.");

        var order = (dto.TurnOrder ?? []).Distinct(StringComparer.Ordinal).ToList();

        // The rotation is the cast for every client that asks the question once. Only a caller that
        // wants somebody in the scene but out of the rotation sends both.
        var participants = dto.ParticipantPersonaIds is {Count: > 0} given
            ? given.Distinct(StringComparer.Ordinal).ToList()
            : order;

        foreach (var personaId in participants)
        {
            if (!await scenes.IsAdoptedAsync(guildId, personaId))
                return Fault("persona_not_adopted", $"Persona '{personaId}' has not been adopted into this guild.");
        }

        if (order.Except(participants, StringComparer.Ordinal).Any())
            return Fault("turn_order_not_in_cast", "The turn order can only name personas in the scene.");

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

            if (dto.Status is { } status)
            {
                state.Status = status;

                // Starting on creation has to open the first turn, or the scene is Active with
                // nobody on the clock and nothing is ever nudged.
                if (status == SceneStatus.Active)
                {
                    var now = DateTimeOffset.UtcNow;
                    var unavailable = await scenes.UnavailablePersonasAsync(guildId, state.Rotation, now);
                    state.StartTurn(unavailable, now);
                }
            }

            ctx.Channels.Add(scene);
            ctx.Channels.Add(ooc);
            ctx.Set<SceneState>().Add(state);

            auditLog.Log(guildId, userId, AuditActionType.ChannelCreated, scene.Id,
                new { ParentChannelId = channelId, Type = nameof(ChannelType.Scene), OocThreadId = ooc.Id });

            // Both halves announce as threads, because both are: a scene the thread fan-out does not
            // carry is one that never reaches a bot's THREAD_CREATE.
            await AnnounceThreadAsync(hub, hydrate, bus, scene, channelId);
            await AnnounceThreadAsync(hub, hydrate, bus, ooc, channelId);

            await scenes.BroadcastCreatedAsync(state, scene);

            return await OkAsync(scenes, state, scene);
        }
        catch (ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }
    }

    /// <summary>
    /// The guild's scenes, so "is the game waiting on me" is one request rather than one per scene.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/scenes")]
    public async Task<IResult> ListAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] PersonaService personas,
        [NotBody] PersonaCastService cast, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        bool waitingOnMe = false, bool includeConcluded = false, bool includeArchived = false,
        int? limit = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // Membership, not ManageScenes: reading the list is what a player does, and the per-channel
        // ViewChannel check below is what actually decides which scenes they see.
        if (await PersonaGate.CheckMembershipAsync(
                permissionService, ctx, guildId, userId, GuildFeatures.Scenes) is { } denied)
        {
            return denied;
        }

        var take = Math.Clamp(limit ?? DefaultListSize, 1, MaxListSize);

        // The set PersonaService already caches per (user, guild) and drops when a grant is revoked,
        // rather than a second answer to "who may speak as this".
        var mine = (await personas.GetUsablePersonasAsync(userId, guildId))
            .Where(p => !p.IsRetired)
            .Select(p => p.PersonaId)
            .ToList();

        if (waitingOnMe && mine.Count == 0)
            return Results.Ok(new SceneListDto { Scenes = [], Truncated = false });

        var rows = await BuildListQuery(
                ctx, guildId, waitingOnMe ? mine : null, includeArchived, includeConcluded)
            .Take(take + 1)
            .ToListAsync();

        var truncated = rows.Count > take;
        var mineSet = mine.ToHashSet(StringComparer.Ordinal);
        var scenes = new List<SceneListItemDto>(Math.Min(rows.Count, take));

        // One lookup for the whole page: the character on the clock is what a row draws, and it is
        // usually somebody else's.
        var onTheClock = await cast.ResolveAsync(guildId, rows
            .Where(row => row.CurrentTurnPersonaId is not null)
            .Select(row => row.CurrentTurnPersonaId!)
            .Distinct(StringComparer.Ordinal)
            .ToList());

        // ViewChannel is resolved per scene after the page is cut, so a caller who cannot see one of
        // them gets a short page rather than somebody else's scene.
        foreach (var row in rows.Take(take))
        {
            if (!await permissionService.CanUserPerformActionAsync(
                    userId, row.ChannelId, Permissions.ViewChannel))
            {
                continue;
            }

            var current = row.CurrentTurnPersonaId is null
                ? null
                : onTheClock.GetValueOrDefault(row.CurrentTurnPersonaId);

            scenes.Add(new SceneListItemDto
            {
                ChannelId = row.ChannelId,
                Name = row.Name,
                ParentChannelId = row.ParentChannelId,
                Status = row.Status,
                CurrentTurnPersonaId = row.CurrentTurnPersonaId,
                CurrentTurnName = current?.Name,
                CurrentTurnAvatarUrl = current?.AvatarUrl,
                CurrentTurnColor = current?.Color,
                TurnStartedAt = row.TurnStartedAt,
                TurnDeadlineAt = row.TurnDeadlineAt,
                TurnNumber = row.TurnNumber,
                PostCount = row.PostCount,
                IsWaitingOnMe = row.CurrentTurnPersonaId is not null
                                && mineSet.Contains(row.CurrentTurnPersonaId),
                ParticipantCount = row.ParticipantCount,
                OocThreadId = row.OocThreadId,
                NudgeCount = row.NudgeCount,
                UpdatedAt = row.UpdatedAt,
            });
        }

        return Results.Ok(new SceneListDto { Scenes = scenes, Truncated = truncated });
    }

    /// <summary>One scene as the list route reads it, before permissions and display data.</summary>
    internal sealed record SceneListRow(
        string ChannelId, string Name, string? ParentChannelId, SceneStatus Status,
        string? CurrentTurnPersonaId, DateTimeOffset? TurnStartedAt, DateTimeOffset? TurnDeadlineAt,
        int TurnNumber, int PostCount, int NudgeCount, int ParticipantCount, string? OocThreadId,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// The scene list's query, extracted so the translation harness can prove it compiles to SQL -
    /// EF InMemory cannot fail on LINQ Npgsql would refuse.
    /// </summary>
    internal static IQueryable<SceneListRow> BuildListQuery(
        MicroserviceContext ctx, string guildId, IReadOnlyCollection<string>? waitingOnPersonaIds,
        bool includeArchived, bool includeConcluded)
    {
        var query = ctx.Set<SceneState>()
            .AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Join(ctx.Channels.AsNoTracking(), s => s.ChannelId, c => c.Id,
                (s, c) => new { State = s, Channel = c });

        if (!includeArchived) query = query.Where(row => !row.Channel.IsArchived);
        if (!includeConcluded) query = query.Where(row => row.State.Status != SceneStatus.Concluded);

        if (waitingOnPersonaIds is not null)
        {
            var mine = waitingOnPersonaIds.ToList();
            query = query.Where(row => row.State.CurrentTurnPersonaId != null
                                       && mine.Contains(row.State.CurrentTurnPersonaId));
        }

        // Scenes on a clock first, soonest due at the top; everything else by recency, which is
        // where a scene waiting on a GM to start it lands.
        return query
            .OrderBy(row => row.State.TurnDeadlineAt == null)
            .ThenBy(row => row.State.TurnDeadlineAt)
            .ThenByDescending(row => row.State.UpdatedAt)
            .Select(row => new SceneListRow(
                row.State.ChannelId,
                row.Channel.Name,
                row.Channel.ParentChannelId,
                row.State.Status,
                row.State.CurrentTurnPersonaId,
                row.State.TurnStartedAt,
                row.State.TurnDeadlineAt,
                row.State.TurnNumber,
                row.State.PostCount,
                row.State.NudgeCount,
                row.State.ParticipantPersonaIds.Count,
                row.State.OocThreadId,
                row.State.UpdatedAt));
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

        return await OkAsync(scenes, found.Value.State, found.Value.Channel);
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

        if (dto.ParticipantPersonaIds is not null)
        {
            var cast = dto.ParticipantPersonaIds.Distinct(StringComparer.Ordinal).ToList();

            foreach (var personaId in cast.Except(state.ParticipantPersonaIds, StringComparer.Ordinal))
            {
                if (!await scenes.IsAdoptedAsync(guildId, personaId))
                    return Fault("persona_not_adopted", $"Persona '{personaId}' has not been adopted into this guild.");

                state.ParticipantPersonaIds.Add(personaId);

                // Matches the add-participant route: a cast change that leaves an explicit rotation
                // untouched would put somebody in the scene who never gets a turn.
                if (dto.TurnOrder is null && state.TurnOrder.Count > 0) state.TurnOrder.Add(personaId);
            }

            var leaving = state.ParticipantPersonaIds.Except(cast, StringComparer.Ordinal).ToList();
            if (leaving.Count > 0)
            {
                var unavailable = await scenes.UnavailablePersonasAsync(guildId, state.Rotation, now);
                foreach (var personaId in leaving) state.RemoveParticipant(personaId, unavailable, now);
            }
        }

        if (dto.TurnOrder is not null)
        {
            if (dto.TurnOrder.Except(state.ParticipantPersonaIds, StringComparer.Ordinal).Any())
                return Fault("turn_order_not_in_cast", "The turn order can only name personas in the scene.");

            state.TurnOrder = [.. dto.TurnOrder.Distinct(StringComparer.Ordinal)];
        }

        if (dto.CurrentTurnPersonaId is not null)
        {
            if (!state.ParticipantPersonaIds.Contains(dto.CurrentTurnPersonaId, StringComparer.Ordinal))
                return Fault("persona_not_in_scene", "That persona is not in this scene.");

            state.TakeTurn(dto.CurrentTurnPersonaId, now);
        }

        if (dto.TurnLengthHours.HasValue) state.TurnLengthHours = dto.TurnLengthHours.Value;
        if (dto.TurnDeadlineAt.HasValue) state.TurnDeadlineAt = dto.TurnDeadlineAt.Value;
        if (dto.ConclusionNote is not null) state.ConclusionNote = dto.ConclusionNote;

        var wasConcluded = state.Status == SceneStatus.Concluded;

        if (dto.Status is { } status) state.Status = status;

        // Starting a scene that nobody has the turn in is the one status change that has to do
        // something, or the first turn never begins and nothing is ever nudged.
        if (state.Status == SceneStatus.Active && state.CurrentTurnPersonaId is null)
        {
            var unavailable = await scenes.UnavailablePersonasAsync(guildId, state.Rotation, now);
            state.StartTurn(unavailable, now);
        }

        state.UpdatedAt = now;

        auditLog.Log(guildId, userId, AuditActionType.ChannelUpdated, sceneChannelId,
            new { Scene = true, state.Status, state.CurrentTurnPersonaId });

        await scenes.BroadcastUpdatedAsync(state);

        // Only on the transition: a PATCH that touches the note of an already concluded scene is
        // an edit to a chronicle, not a second ending.
        if (!wasConcluded && state.Status == SceneStatus.Concluded)
            await scenes.BroadcastConcludedAsync(state);

        return await OkAsync(scenes, state, channel);
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
            return Fault("persona_not_adopted", "That persona has not been adopted into this guild.");

        if (state.ParticipantPersonaIds.Contains(dto.PersonaId, StringComparer.Ordinal))
            return Fault("persona_already_in_scene", "That persona is already in this scene.", StatusCodes.Status409Conflict);

        state.ParticipantPersonaIds.Add(dto.PersonaId);

        if (state.TurnOrder.Count > 0)
        {
            var position = Math.Clamp(dto.Position ?? state.TurnOrder.Count, 0, state.TurnOrder.Count);
            state.TurnOrder.Insert(position, dto.PersonaId);
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;

        await scenes.BroadcastUpdatedAsync(state);

        return await OkAsync(scenes, state, channel);
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

        return await OkAsync(scenes, state, channel);
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
            return Fault("scene_not_active", "The turn only moves while a scene is active.");

        var isGameMaster = await permissionService.CanUserPerformActionOnGuildAsync(
            userId, guildId, ModulePermissions.ManageScenes);

        if (!isGameMaster && !await AnswersForCurrentTurnAsync(scenes, state, userId))
            return Results.Forbid();

        await scenes.AdvanceAsync(state, DateTimeOffset.UtcNow);

        return await OkAsync(scenes, state, channel);
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
            return Fault("scene_not_active", "The turn only moves while a scene is active.");

        await scenes.AdvanceAsync(state, DateTimeOffset.UtcNow);

        return await OkAsync(scenes, state, channel);
    }

    /// <summary>
    /// Chases the current turn now. The sweep runs every quarter hour and holds a nudge through the
    /// guild's quiet hours; a GM looking at a stalled scene should not have to wait for either.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/turn/nudge")]
    public async Task<IResult> NudgeTurnAsync(string guildId, string sceneChannelId,
        [NotBody] GuildPermissionService permissionService, [NotBody] SceneService scenes,
        [NotBody] SceneNudgeService nudges, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var found = await ResolveAsync(ctx, scenes, guildId, sceneChannelId);
        if (found is null) return Results.NotFound();

        var (channel, state) = found.Value;

        if (!await nudges.NudgeNowAsync(state, DateTimeOffset.UtcNow))
            return Fault("no_turn_to_nudge", "There is no turn to chase in this scene.");

        return await OkAsync(scenes, state, channel);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A refusal a client can act on: a stable code to branch on, a sentence to show.</summary>
    private static IResult Fault(string error, string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error, message }, statusCode: status);

    /// <summary>The scene as clients read it, cast and absences included.</summary>
    private static async Task<IResult> OkAsync(SceneService scenes, SceneState state, Channel channel)
    {
        var participants = await scenes.ParticipantsAsync(state, DateTimeOffset.UtcNow);

        return Results.Ok(SceneDto.From(state, channel, participants));
    }

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
