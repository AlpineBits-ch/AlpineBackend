using System.ComponentModel.DataAnnotations.Schema;
using Guild.Domain.Enums;

namespace Guild.Domain.Entity;

public class CreateSceneStateParams
{
    public string ChannelId { get; init; } = null!;
    public string GuildId { get; init; } = null!;
    public string? OocThreadId { get; init; }
    public int? TurnLengthHours { get; init; }
    public List<string> ParticipantPersonaIds { get; init; } = [];
    public SceneJoinPolicy JoinPolicy { get; init; } = SceneJoinPolicy.Open;
    public SceneVisibility Visibility { get; init; } = SceneVisibility.Everyone;
}

/// <summary>
/// A scene's cast and turn order, 1:1 with its channel and keyed by its id in the shape
/// <c>ForumConfig</c> already uses. Channel.cs justifies the forum columns living on the channel row
/// on the grounds that each is a sort key or a filter on the forum listing; none of these is, and
/// two are arrays, so the same reasoning puts them here.
/// </summary>
public class SceneState
{
    /// <summary>PK and FK both - the Scene channel this is the state of.</summary>
    public string ChannelId { get; set; } = null!;

    public string GuildId { get; set; } = null!;

    /// <summary>Every character in the scene, whether or not it is currently in the rotation.</summary>
    public List<string> ParticipantPersonaIds { get; set; } = [];

    /// <summary>The rotation, as an ordered subset of the participants. Empty means the
    /// participants in the order they joined.</summary>
    public List<string> TurnOrder { get; set; } = [];

    public string? CurrentTurnPersonaId { get; set; }

    /// <summary>When the current turn opened. A deadline on its own cannot say how much of a turn is
    /// spent, which is the clock every client draws.</summary>
    public DateTimeOffset? TurnStartedAt { get; set; }

    /// <summary>Which turn the scene is on, counting from one. "Turn 47" is what makes a scene read
    /// as a game rather than as a chat.</summary>
    public int TurnNumber { get; set; }

    /// <summary>How many messages the scene has seen, so a concluded campaign can say what it was
    /// worth.</summary>
    public int PostCount { get; set; }

    /// <summary>The closing line a GM writes when ending a scene.</summary>
    public string? ConclusionNote { get; set; }

    /// <summary>When the current turn goes stale. Null means this scene is not on a clock and is
    /// never nudged.</summary>
    public DateTimeOffset? TurnDeadlineAt { get; set; }

    /// <summary>How long each turn gets, used to set the next deadline when the turn advances on
    /// its own. Without it an automatic advance would leave the scene un-nudgeable after the first
    /// post.</summary>
    public int? TurnLengthHours { get; set; }

    public SceneStatus Status { get; set; } = SceneStatus.Open;

    /// <summary>Whether a player brings a character in themselves or has to ask.</summary>
    public SceneJoinPolicy JoinPolicy { get; set; } = SceneJoinPolicy.Open;

    /// <summary>Who may see the scene. <see cref="SceneVisibility.Cast"/> with
    /// <see cref="SceneJoinPolicy.Open"/> is refused: walking into a scene you cannot see is not a
    /// state anything can act on.</summary>
    public SceneVisibility Visibility { get; set; } = SceneVisibility.Everyone;

    /// <summary>The out-of-character companion thread, created and linked at scene creation because
    /// every guide on running roleplay says to pair them and every server does it by hand.</summary>
    public string? OocThreadId { get; set; }

    /// <summary>How many times the current turn has been nudged. Reset by every advance, and the
    /// second one is what escalates to the GM.</summary>
    public int NudgeCount { get; set; }

    public DateTimeOffset? LastNudgedAt { get; set; }

    /// <summary>The archive folder this scene is filed under. Null means unfiled.</summary>
    public string? FolderId { get; set; }

    /// <summary>When the scene ended. UpdatedAt cannot answer this: an edit to a concluded scene's
    /// note moves it, and the archive dates a campaign by when it finished.</summary>
    public DateTimeOffset? ConcludedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static SceneState Create(CreateSceneStateParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new SceneState
        {
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            OocThreadId = @params.OocThreadId,
            TurnLengthHours = @params.TurnLengthHours,
            ParticipantPersonaIds = [.. @params.ParticipantPersonaIds.Distinct(StringComparer.Ordinal)],
            JoinPolicy = @params.JoinPolicy,
            Visibility = @params.Visibility,
            CreatedAt = date,
            UpdatedAt = date,
        };
    }

    /// <summary>
    /// Whether a policy and a visibility describe a scene anybody can act on. Cast-only with an
    /// open table is the one pair that does not: nobody outside the cast can find the scene to walk
    /// into it.
    /// </summary>
    /// <param name="policy">The join policy.</param>
    /// <param name="visibility">The visibility.</param>
    /// <returns>False for the one refused pair.</returns>
    public static bool IsAccessPairLegal(SceneJoinPolicy policy, SceneVisibility visibility) =>
        visibility != SceneVisibility.Cast || policy != SceneJoinPolicy.Open;

    /// <summary>Whether this character is in the cast.</summary>
    /// <param name="personaId">The character.</param>
    /// <returns>True when the scene lists it as a participant.</returns>
    public bool IsInCast(string? personaId) =>
        personaId is not null
        && ParticipantPersonaIds.Contains(personaId, StringComparer.Ordinal);

    /// <summary>Puts a character in the cast and at the end of the rotation.</summary>
    /// <param name="personaId">The character joining.</param>
    /// <param name="now">The instant it joined.</param>
    /// <returns>False when it was already in the scene.</returns>
    public bool AddParticipant(string personaId, DateTimeOffset now)
    {
        if (IsInCast(personaId)) return false;

        ParticipantPersonaIds.Add(personaId);

        // Only when a rotation was set by hand: an empty TurnOrder already means the cast in the
        // order it was assembled, so appending there would freeze it.
        if (TurnOrder.Count > 0) TurnOrder.Add(personaId);

        UpdatedAt = now;
        return true;
    }

    /// <summary>The rotation as it actually resolves: the explicit turn order when one is set,
    /// otherwise the participants in the order they were added.</summary>
    [NotMapped]
    public IReadOnlyList<string> Rotation => TurnOrder.Count > 0 ? TurnOrder : ParticipantPersonaIds;

    /// <summary>Whether it is this character's turn to post.</summary>
    /// <param name="personaId">The character that just spoke.</param>
    /// <returns>True when the scene is being played and the turn is theirs.</returns>
    public bool IsCurrentTurn(string? personaId) =>
        Status == SceneStatus.Active
        && !string.IsNullOrWhiteSpace(personaId)
        && string.Equals(CurrentTurnPersonaId, personaId, StringComparison.Ordinal);

    /// <summary>
    /// Whose turn comes after <paramref name="current"/>, walking the rotation and stepping over
    /// anyone unavailable.
    /// </summary>
    /// <param name="current">The character whose turn is ending, or null to start from the top.</param>
    /// <param name="unavailable">Characters whose players have declared an absence covering now.</param>
    /// <returns>The next character, or null when the whole rotation is away.</returns>
    public string? NextTurn(string? current, IReadOnlyCollection<string> unavailable)
    {
        var rotation = Rotation;
        if (rotation.Count == 0) return null;

        var from = current is null ? -1 : rotation.ToList().IndexOf(current);

        for (var step = 1; step <= rotation.Count; step++)
        {
            var candidate = rotation[(from + step) % rotation.Count];
            if (!unavailable.Contains(candidate)) return candidate;
        }

        // Everybody is away. The turn is handed to nobody rather than to somebody on holiday, which
        // leaves the scene visibly waiting on its GM instead of silently nudging an empty chair.
        return null;
    }

    /// <summary>
    /// Moves the turn on and re-arms the clock.
    /// </summary>
    /// <param name="unavailable">Characters whose players have declared an absence covering now.</param>
    /// <param name="now">The instant the turn changed.</param>
    /// <returns>The character the turn passed to, or null when nobody was available.</returns>
    public string? Advance(IReadOnlyCollection<string> unavailable, DateTimeOffset now)
    {
        var next = NextTurn(CurrentTurnPersonaId, unavailable);

        CurrentTurnPersonaId = next;
        TurnDeadlineAt = next is not null && TurnLengthHours is > 0
            ? now.AddHours(TurnLengthHours.Value)
            : null;
        TurnStartedAt = next is not null ? now : null;
        if (next is not null) TurnNumber++;
        NudgeCount = 0;
        LastNudgedAt = null;
        UpdatedAt = now;

        return next;
    }

    /// <summary>
    /// Opens the first turn of a scene that has not had one, which is what starting a scene has to
    /// do or nothing is ever nudged.
    /// </summary>
    /// <param name="unavailable">Characters whose players have declared an absence covering now.</param>
    /// <param name="now">The instant the scene started.</param>
    /// <returns>The character the turn opened on, or null when the whole rotation is away.</returns>
    public string? StartTurn(IReadOnlyCollection<string> unavailable, DateTimeOffset now)
    {
        var next = NextTurn(null, unavailable);
        CurrentTurnPersonaId = next;
        if (next is null) return null;

        TakeTurn(next, now);

        // Left alone when the scene is not on a clock: a deadline set by hand is the GM's, and
        // starting the scene is not a reason to drop it.
        if (TurnLengthHours is > 0) TurnDeadlineAt = now.AddHours(TurnLengthHours.Value);

        return next;
    }

    /// <summary>Hands the turn to a named character, for the cases the rotation cannot express.</summary>
    /// <param name="personaId">The character taking the turn.</param>
    /// <param name="now">The instant the turn opened.</param>
    public void TakeTurn(string personaId, DateTimeOffset now)
    {
        CurrentTurnPersonaId = personaId;
        TurnStartedAt = now;
        TurnNumber++;
        NudgeCount = 0;
        LastNudgedAt = null;
        UpdatedAt = now;
    }

    /// <summary>Drops a character from the cast, and hands the turn on when it was theirs.</summary>
    /// <param name="personaId">The character leaving the scene.</param>
    /// <param name="unavailable">Characters whose players have declared an absence covering now.</param>
    /// <param name="now">The instant the character left.</param>
    /// <returns>True when the character was in the scene.</returns>
    public bool RemoveParticipant(
        string personaId, IReadOnlyCollection<string> unavailable, DateTimeOffset now)
    {
        if (!ParticipantPersonaIds.Remove(personaId)) return false;

        TurnOrder.Remove(personaId);

        // Order matters: the character has to be out of the rotation before the turn moves, or it
        // can be handed straight back to the person who just left.
        if (string.Equals(CurrentTurnPersonaId, personaId, StringComparison.Ordinal))
            Advance(unavailable, now);

        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Ends the scene for good.
    /// </summary>
    /// <param name="note">The closing line, or null to keep the existing one.</param>
    /// <param name="now">The instant the scene ended.</param>
    public void Conclude(string? note, DateTimeOffset now)
    {
        Status = SceneStatus.Concluded;
        if (note is not null) ConclusionNote = note;

        // Stamped once. A later edit to the note is an edit to a chronicle, not a second ending.
        ConcludedAt ??= now;

        CurrentTurnPersonaId = null;
        TurnStartedAt = null;
        TurnDeadlineAt = null;
        NudgeCount = 0;
        LastNudgedAt = null;
        UpdatedAt = now;
    }

    /// <summary>Records that the current turn has been chased once more.</summary>
    /// <param name="now">The instant the nudge went out.</param>
    /// <returns>True when this nudge is the escalation, meaning the turn has now been missed twice.</returns>
    public bool RecordNudge(DateTimeOffset now)
    {
        NudgeCount++;
        LastNudgedAt = now;
        UpdatedAt = now;

        return NudgeCount >= 2;
    }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<SceneState>(sceneBuilder =>
// {
//     sceneBuilder.HasKey(x => x.ChannelId);
//
//     sceneBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithOne()
//         .HasForeignKey<SceneState>(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // What the stale-turn sweep asks: which scenes are being played and overdue.
//     sceneBuilder.HasIndex(x => new { x.Status, x.TurnDeadlineAt });
// });
// DbSet: public DbSet<SceneState> SceneStates { get; set; }
// MapEnum: options.MapEnum<SceneStatus>();
// MapEnum: options.MapEnum<SceneJoinPolicy>();
// MapEnum: options.MapEnum<SceneVisibility>();
