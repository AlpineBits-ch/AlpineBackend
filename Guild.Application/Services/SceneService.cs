using Echo.Realtime;
using Guild.Application.Dtos.Response;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Turn order for scenes: who is up, who is away, and telling the room when that changes.
/// </summary>
public class SceneService(
    MicroserviceContext ctx,
    PersonaMentionService personaMentions,
    PersonaCastService cast,
    GuildHydrateService hydrate,
    IHubContext<EchoRealtimeHub> hub)
{
    /// <summary>The turn moved, for whatever reason.</summary>
    public const string TurnChangedEvent = "guild.SceneTurnChanged";

    /// <summary>The cast, the order, the status or the clock changed.</summary>
    public const string UpdatedEvent = "guild.SceneUpdated";

    /// <summary>A turn has gone stale, addressed to whoever can do something about it.</summary>
    public const string NudgeEvent = "guild.SceneTurnNudge";

    /// <summary>A scene was opened, with its cast and its out-of-character thread.</summary>
    public const string CreatedEvent = "guild.SceneCreated";

    /// <summary>A scene finished for good.</summary>
    public const string ConcludedEvent = "guild.SceneConcluded";

    /// <summary>Somebody wants a character in a closed scene, addressed to whoever can say yes.</summary>
    public const string JoinRequestedEvent = "guild.SceneJoinRequested";

    /// <summary>One ask was answered, addressed to the player and to the GMs.</summary>
    public const string JoinRequestResolvedEvent = "guild.SceneJoinRequestResolved";

    /// <summary>The scene's turn state, tracked so a caller can write to it.</summary>
    /// <param name="channelId">The scene channel.</param>
    /// <returns>The state row, or null when the channel is not a scene.</returns>
    public Task<SceneState?> GetAsync(string channelId) =>
        ctx.Set<SceneState>().FirstOrDefaultAsync(s => s.ChannelId == channelId);

    /// <summary>
    /// Which of these characters cannot take a turn right now: nobody answers for them here, or
    /// everybody who does has declared an absence covering <paramref name="at"/>.
    /// </summary>
    /// <param name="guildId">The guild the scene is in.</param>
    /// <param name="personaIds">The characters to check.</param>
    /// <param name="at">The instant the turn would start.</param>
    /// <returns>The characters to step over.</returns>
    public async Task<HashSet<string>> UnavailablePersonasAsync(
        string guildId, IReadOnlyCollection<string> personaIds, DateTimeOffset at)
    {
        var (away, unplayable) = await SplitAvailabilityAsync(guildId, personaIds, at);

        away.UnionWith(unplayable);
        return away;
    }

    /// <summary>
    /// Which of these characters are being stepped over because their players said so, as opposed to
    /// because nobody answers for them.
    /// </summary>
    /// <param name="guildId">The guild the scene is in.</param>
    /// <param name="personaIds">The characters to check.</param>
    /// <param name="at">The instant to judge the absences at.</param>
    /// <returns>The characters every player of whom has declared an absence covering that instant.</returns>
    public async Task<HashSet<string>> AwayPersonasAsync(
        string guildId, IReadOnlyCollection<string> personaIds, DateTimeOffset at) =>
        (await SplitAvailabilityAsync(guildId, personaIds, at)).Away;

    /// <summary>The two reasons a character cannot take a turn, kept apart because only one of them
    /// is something a client may say out loud.</summary>
    private async Task<(HashSet<string> Away, HashSet<string> Unplayable)> SplitAvailabilityAsync(
        string guildId, IReadOnlyCollection<string> personaIds, DateTimeOffset at)
    {
        var away = new HashSet<string>(StringComparer.Ordinal);
        var unplayable = new HashSet<string>(StringComparer.Ordinal);
        if (personaIds.Count == 0) return (away, unplayable);

        var owners = await OwnersByPersonaAsync(guildId, personaIds);

        // Narrowed in SQL and then decided by MemberAbsence.Covers, so the endpoint boundary rule
        // (EndAt is exclusive) is stated in exactly one place.
        var absences = await ctx.MemberAbsences
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.StartAt <= at && a.EndAt > at)
            .ToListAsync();

        var absent = absences
            .Where(a => a.Covers(at))
            .Select(a => a.UserId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var personaId in personaIds)
        {
            // A character nobody answers for is as unable to take a turn as one whose player is on
            // holiday - it has been retired, or was never adopted into this guild.
            if (!owners.TryGetValue(personaId, out var players) || players.Count == 0)
            {
                unplayable.Add(personaId);
                continue;
            }

            if (players.All(absent.Contains)) away.Add(personaId);
        }

        return (away, unplayable);
    }

    /// <summary>
    /// The cast as a client draws it: each character's name, avatar and colour, plus whether the
    /// rotation is currently stepping over it. Denormalized deliberately - a scene renders other
    /// players' characters, and none of them can be resolved from the caller's own persona list.
    /// </summary>
    /// <param name="scene">The scene's turn state.</param>
    /// <param name="at">The instant to judge absences at.</param>
    /// <returns>One entry per participant, in cast order.</returns>
    public async Task<List<SceneParticipantDto>> ParticipantsAsync(SceneState scene, DateTimeOffset at)
    {
        var participants = scene.ParticipantPersonaIds;
        if (participants.Count == 0) return [];

        var away = await AwayPersonasAsync(scene.GuildId, participants, at);
        var rendered = await cast.ResolveAsync(scene.GuildId, participants);

        return participants
            .Select(personaId =>
            {
                var member = rendered.GetValueOrDefault(personaId);
                return new SceneParticipantDto
                {
                    PersonaId = personaId,
                    Name = member?.Name ?? "",
                    AvatarUrl = member?.AvatarUrl,
                    Color = member?.Color,
                    Tag = member?.Tag,
                    IsRetired = member?.IsRetired ?? false,
                    IsAway = away.Contains(personaId),
                    IsCurrentTurn = string.Equals(
                        scene.CurrentTurnPersonaId, personaId, StringComparison.Ordinal),
                };
            })
            .ToList();
    }

    /// <summary>
    /// Who answers for each of these characters in this guild - the owner for a personal one, every
    /// current grant holder for a shared one.
    /// </summary>
    /// <param name="guildId">The guild the scene is in.</param>
    /// <param name="personaIds">The characters to resolve.</param>
    /// <returns>Players keyed by character, with characters nobody answers for absent.</returns>
    public Task<Dictionary<string, List<string>>> OwnersByPersonaAsync(
        string guildId, IReadOnlyCollection<string> personaIds) =>
        personaMentions.OwnersByPersonaAsync(guildId, personaIds);

    /// <summary>
    /// Hands the turn on because the character whose turn it was posted. This is what makes a scene
    /// feel like play rather than administration, so it runs off the message rather than off a
    /// button.
    /// </summary>
    /// <param name="scene">The scene's turn state, tracked.</param>
    /// <param name="personaId">The character the message went out as.</param>
    /// <param name="now">The instant the message was created.</param>
    /// <returns>True when the turn moved.</returns>
    public async Task<bool> AdvanceOnPostAsync(SceneState scene, string? personaId, DateTimeOffset now)
    {
        if (!scene.IsCurrentTurn(personaId)) return false;

        var previous = scene.CurrentTurnPersonaId;
        var unavailable = await UnavailablePersonasAsync(scene.GuildId, scene.Rotation, now);

        scene.Advance(unavailable, now);
        await BroadcastTurnAsync(scene, previous);

        return true;
    }

    /// <summary>Moves the turn on and tells the room.</summary>
    /// <param name="scene">The scene's turn state, tracked.</param>
    /// <param name="now">The instant the turn changed.</param>
    /// <returns>The character the turn passed to, or null when the whole rotation is away.</returns>
    public async Task<string?> AdvanceAsync(SceneState scene, DateTimeOffset now)
    {
        var previous = scene.CurrentTurnPersonaId;
        var unavailable = await UnavailablePersonasAsync(scene.GuildId, scene.Rotation, now);

        var next = scene.Advance(unavailable, now);
        await BroadcastTurnAsync(scene, previous);

        return next;
    }

    /// <summary>Whether this character has been adopted into the guild, which is what makes it
    /// castable at all.</summary>
    /// <param name="guildId">The guild the scene is in.</param>
    /// <param name="personaId">The character being added.</param>
    /// <returns>True when a profile for the pair exists.</returns>
    public Task<bool> IsAdoptedAsync(string guildId, string personaId) =>
        ctx.Set<PersonaGuildProfile>().AnyAsync(p => p.GuildId == guildId && p.PersonaId == personaId);

    /// <summary>
    /// Tells the guild a scene was opened. The pair of <c>guild.ThreadCreated</c> events that go
    /// out alongside say two threads appeared; this is the one that says a game started.
    /// </summary>
    /// <param name="scene">The scene's turn state.</param>
    /// <param name="channel">The scene channel.</param>
    public async Task BroadcastCreatedAsync(SceneState scene, Channel channel) =>
        await BroadcastAsync(scene.GuildId, CreatedEvent, new
        {
            GuildId = scene.GuildId,
            ChannelId = scene.ChannelId,
            ParentChannelId = channel.ParentChannelId,
            Name = channel.Name,
            OocThreadId = scene.OocThreadId,
            Status = scene.Status.ToString(),
            JoinPolicy = scene.JoinPolicy.ToString(),
            Visibility = scene.Visibility.ToString(),
            ParticipantPersonaIds = scene.ParticipantPersonaIds,
            TurnOrder = scene.TurnOrder,
            CurrentTurnPersonaId = scene.CurrentTurnPersonaId,
            TurnStartedAt = scene.TurnStartedAt,
            TurnDeadlineAt = scene.TurnDeadlineAt,
            TurnNumber = scene.TurnNumber,
            TurnLengthHours = scene.TurnLengthHours,
        });

    /// <summary>
    /// Tells the guild a scene finished. Conclusion is what a chronicle exports and what a client
    /// stops nudging on, so it is its own event rather than a status field somebody has to diff.
    /// </summary>
    /// <param name="scene">The scene's turn state.</param>
    public async Task BroadcastConcludedAsync(SceneState scene) =>
        await BroadcastAsync(scene.GuildId, ConcludedEvent, new
        {
            GuildId = scene.GuildId,
            ChannelId = scene.ChannelId,
            Status = scene.Status.ToString(),
            ConclusionNote = scene.ConclusionNote,
            TurnNumber = scene.TurnNumber,
            PostCount = scene.PostCount,
            ConcludedAt = scene.ConcludedAt ?? scene.UpdatedAt,
        });

    /// <summary>Tells the guild the turn moved.</summary>
    /// <param name="scene">The scene's turn state.</param>
    /// <param name="previousPersonaId">Whose turn it was, so a client can render the handover.</param>
    public async Task BroadcastTurnAsync(SceneState scene, string? previousPersonaId) =>
        await BroadcastAsync(scene.GuildId, TurnChangedEvent, new
        {
            GuildId = scene.GuildId,
            ChannelId = scene.ChannelId,
            PreviousPersonaId = previousPersonaId,
            CurrentTurnPersonaId = scene.CurrentTurnPersonaId,
            // The clock is drawn from both ends, so an event that moved the turn carries both or the
            // receiving client has to guess when this one opened.
            TurnStartedAt = scene.TurnStartedAt,
            TurnDeadlineAt = scene.TurnDeadlineAt,
            TurnNumber = scene.TurnNumber,
            Status = scene.Status.ToString(),
        });

    /// <summary>Tells the guild the scene itself changed.</summary>
    /// <param name="scene">The scene's turn state.</param>
    public async Task BroadcastUpdatedAsync(SceneState scene) =>
        await BroadcastAsync(scene.GuildId, UpdatedEvent, new
        {
            GuildId = scene.GuildId,
            ChannelId = scene.ChannelId,
            Status = scene.Status.ToString(),
            JoinPolicy = scene.JoinPolicy.ToString(),
            Visibility = scene.Visibility.ToString(),
            ParticipantPersonaIds = scene.ParticipantPersonaIds,
            TurnOrder = scene.TurnOrder,
            CurrentTurnPersonaId = scene.CurrentTurnPersonaId,
            TurnStartedAt = scene.TurnStartedAt,
            TurnDeadlineAt = scene.TurnDeadlineAt,
            TurnNumber = scene.TurnNumber,
            PostCount = scene.PostCount,
            ConclusionNote = scene.ConclusionNote,
            OocThreadId = scene.OocThreadId,
            FolderId = scene.FolderId,
        });

    private async Task BroadcastAsync(string guildId, string eventName, object payload)
    {
        var presence = await hydrate.GetGuildPresenceAsync(guildId);
        if (presence.Count == 0) return;

        await hub.Clients.Users(presence.Select(p => p.UserId).ToList()).SendAsync(eventName, payload);
    }
}
