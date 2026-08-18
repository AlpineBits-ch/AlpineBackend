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

    public string? CurrentTurnPersonaId { get; set; }
    public DateTimeOffset? TurnDeadlineAt { get; set; }
    public int? TurnLengthHours { get; set; }

    /// <summary>The out-of-character companion thread.</summary>
    public string? OocThreadId { get; set; }

    /// <summary>How many times the current turn has been chased; the second one goes to the GM
    /// as well.</summary>
    public int NudgeCount { get; set; }

    public static SceneDto From(SceneState scene, Guild.Domain.Aggregates.Channel channel) => new()
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
        CurrentTurnPersonaId = scene.CurrentTurnPersonaId,
        TurnDeadlineAt = scene.TurnDeadlineAt,
        TurnLengthHours = scene.TurnLengthHours,
        OocThreadId = scene.OocThreadId,
        NudgeCount = scene.NudgeCount,
    };
}
