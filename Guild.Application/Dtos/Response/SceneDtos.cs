using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>A scene: the channel it is, the cast it has, and whose turn it is.</summary>
public class SceneDto
{
    /// <summary>The scene channel's id, which is also the key of its turn state.</summary>
    public string ChannelId { get; set; } = null!;

    public string GuildId { get; set; } = null!;

    /// <summary>The scene's title, carried here so a client listing scenes needs one call.</summary>
    public string Name { get; set; } = null!;

    public string? ParentChannelId { get; set; }
    public string? CreatedByUserId { get; set; }
    public bool IsArchived { get; set; }

    public SceneStatus Status { get; set; }
    public List<string> ParticipantPersonaIds { get; set; } = [];

    /// <summary>The rotation. Empty means the cast in the order it was assembled.</summary>
    public List<string> TurnOrder { get; set; } = [];

    /// <summary>The cast with its display data, so rendering another player's character costs no
    /// second call.</summary>
    public List<SceneParticipantDto> Participants { get; set; } = [];

    public string? CurrentTurnPersonaId { get; set; }

    /// <summary>When the current turn opened, which is the other end of the clock clients draw.</summary>
    public DateTimeOffset? TurnStartedAt { get; set; }

    public DateTimeOffset? TurnDeadlineAt { get; set; }
    public int? TurnLengthHours { get; set; }

    /// <summary>Which turn the scene is on, counting from one.</summary>
    public int TurnNumber { get; set; }

    /// <summary>How many messages the scene has seen.</summary>
    public int PostCount { get; set; }

    /// <summary>The closing line the GM wrote when ending the scene.</summary>
    public string? ConclusionNote { get; set; }

    /// <summary>The out-of-character companion thread.</summary>
    public string? OocThreadId { get; set; }

    /// <summary>How many times the current turn has been chased; the second one goes to the GM
    /// as well.</summary>
    public int NudgeCount { get; set; }

    /// <summary>
    /// Which of the cast the rotation is currently stepping over because their players declared an
    /// absence, so a skip renders as deliberate. Characters, never players: no dates and no note,
    /// because either would say which member's absence this is.
    /// </summary>
    public List<string> AwayPersonaIds { get; set; } = [];

    /// <summary>The archive folder this scene is filed under. Null means unfiled.</summary>
    public string? FolderId { get; set; }

    public List<string> TagIds { get; set; } = [];

    /// <summary>Null on a scene that has not ended, and on one that ended before the column did.</summary>
    public DateTimeOffset? ConcludedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static SceneDto From(
        SceneState scene, Guild.Domain.Aggregates.Channel channel,
        IReadOnlyCollection<SceneParticipantDto>? participants = null,
        IReadOnlyCollection<string>? tagIds = null) => new()
    {
        ChannelId = scene.ChannelId,
        GuildId = scene.GuildId,
        Name = channel.Name,
        ParentChannelId = channel.ParentChannelId,
        CreatedByUserId = channel.CreatedByUserId,
        IsArchived = channel.IsArchived,
        Status = scene.Status,
        ParticipantPersonaIds = [.. scene.ParticipantPersonaIds],
        TurnOrder = [.. scene.TurnOrder],
        Participants = participants is null ? [] : [.. participants],
        CurrentTurnPersonaId = scene.CurrentTurnPersonaId,
        TurnStartedAt = scene.TurnStartedAt,
        TurnDeadlineAt = scene.TurnDeadlineAt,
        TurnLengthHours = scene.TurnLengthHours,
        TurnNumber = scene.TurnNumber,
        PostCount = scene.PostCount,
        ConclusionNote = scene.ConclusionNote,
        OocThreadId = scene.OocThreadId,
        NudgeCount = scene.NudgeCount,
        AwayPersonaIds = participants is null
            ? []
            : [.. participants.Where(p => p.IsAway).Select(p => p.PersonaId)],
        FolderId = scene.FolderId,
        TagIds = tagIds is null ? [] : [.. tagIds],
        ConcludedAt = scene.ConcludedAt,
        CreatedAt = scene.CreatedAt,
    };
}

/// <summary>
/// One character in a scene, with what it takes to draw it. Nothing here says who plays it - see
/// docs/specs/roleplay-guilds.md §2.1.
/// </summary>
public class SceneParticipantDto
{
    public string PersonaId { get; set; } = null!;

    /// <summary>The per-guild display name, empty for a character this guild no longer adopts.</summary>
    public string Name { get; set; } = "";

    public string? AvatarUrl { get; set; }
    public string? Color { get; set; }
    public string? Tag { get; set; }
    public bool IsRetired { get; set; }

    /// <summary>Whether the rotation is stepping over this character because its players declared an
    /// absence.</summary>
    public bool IsAway { get; set; }

    public bool IsCurrentTurn { get; set; }
}

/// <summary>
/// One row of a guild's scene list: enough to answer "is the game waiting on me" and to render the
/// row, without a second call for each scene.
/// </summary>
public class SceneListItemDto
{
    public string ChannelId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? ParentChannelId { get; set; }
    public SceneStatus Status { get; set; }

    public string? CurrentTurnPersonaId { get; set; }

    /// <summary>The character whose turn it is, named, so a row needs no lookup of its own.</summary>
    public string? CurrentTurnName { get; set; }

    public string? CurrentTurnAvatarUrl { get; set; }
    public string? CurrentTurnColor { get; set; }

    public DateTimeOffset? TurnStartedAt { get; set; }
    public DateTimeOffset? TurnDeadlineAt { get; set; }
    public int TurnNumber { get; set; }
    public int PostCount { get; set; }

    /// <summary>Whether the turn belongs to a character the caller may speak as.</summary>
    public bool IsWaitingOnMe { get; set; }

    /// <summary>Size of the cast, which is all a list row needs of it.</summary>
    public int ParticipantCount { get; set; }

    /// <summary>The out-of-character companion thread.</summary>
    public string? OocThreadId { get; set; }

    /// <summary>How many times the current turn has been chased.</summary>
    public int NudgeCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The archive folder this scene is filed under. Null means unfiled.</summary>
    public string? FolderId { get; set; }

    public List<string> TagIds { get; set; } = [];

    /// <summary>Null on a scene that has not ended, and on one that ended before the column did.</summary>
    public DateTimeOffset? ConcludedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A guild's scenes, newest activity first.</summary>
public class SceneListDto
{
    public required IReadOnlyList<SceneListItemDto> Scenes { get; init; }

    /// <summary>True when more scenes matched than the route returns.</summary>
    public required bool Truncated { get; init; }
}
