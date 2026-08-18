using Echo.Realtime;
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
    GuildHydrateService hydrate,
    IHubContext<EchoRealtimeHub> hub)
{
    /// <summary>The turn moved, for whatever reason.</summary>
    public const string TurnChangedEvent = "guild.SceneTurnChanged";

    /// <summary>The cast, the order, the status or the clock changed.</summary>
    public const string UpdatedEvent = "guild.SceneUpdated";

    /// <summary>A turn has gone stale, addressed to whoever can do something about it.</summary>
    public const string NudgeEvent = "guild.SceneTurnNudge";

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
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        if (personaIds.Count == 0) return unavailable;

        var owners = await OwnersByPersonaAsync(guildId, personaIds);

        // Narrowed in SQL and then decided by MemberAbsence.Covers, so the endpoint boundary rule
        // (EndAt is exclusive) is stated in exactly one place.
        var absences = await ctx.MemberAbsences
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.StartAt <= at && a.EndAt > at)
            .ToListAsync();

        var away = absences
            .Where(a => a.Covers(at))
            .Select(a => a.UserId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var personaId in personaIds)
        {
            // A character nobody answers for is as unable to take a turn as one whose player is on
            // holiday - it has been retired, or was never adopted into this guild.
            if (!owners.TryGetValue(personaId, out var players) || players.Count == 0)
            {
                unavailable.Add(personaId);
                continue;
            }

            if (players.All(away.Contains)) unavailable.Add(personaId);
        }

        return unavailable;
    }

    /// <summary>
    /// Who answers for each of these characters in this guild - the owner for a personal one, every
    /// current grant holder for a shared one.
    /// </summary>
    /// <param name="guildId">The guild the scene is in.</param>
    /// <param name="personaIds">The characters to resolve.</param>
    /// <returns>Players keyed by character, with characters nobody answers for absent.</returns>
    public async Task<Dictionary<string, List<string>>> OwnersByPersonaAsync(
        string guildId, IReadOnlyCollection<string> personaIds)
    {
        // authorId is empty rather than a real user: the resolver drops the author from its own
        // results, and a nudge has no author to drop.
        var targets = await personaMentions.ResolveAsync(guildId, string.Empty, personaIds);

        return targets
            .GroupBy(t => t.PersonaId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(t => t.UserId).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }

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
            TurnDeadlineAt = scene.TurnDeadlineAt,
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
            ParticipantPersonaIds = scene.ParticipantPersonaIds,
            TurnOrder = scene.TurnOrder,
            CurrentTurnPersonaId = scene.CurrentTurnPersonaId,
            TurnDeadlineAt = scene.TurnDeadlineAt,
            OocThreadId = scene.OocThreadId,
        });

    private async Task BroadcastAsync(string guildId, string eventName, object payload)
    {
        var presence = await hydrate.GetGuildPresenceAsync(guildId);
        if (presence.Count == 0) return;

        await hub.Clients.Users(presence.Select(p => p.UserId).ToList()).SendAsync(eventName, payload);
    }
}
