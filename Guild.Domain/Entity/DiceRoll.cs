using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateDiceRollParams
{
    public string MessageId { get; init; } = null!;
    public string GuildId { get; init; } = null!;
    public string ChannelId { get; init; } = null!;
    public string RollerUserId { get; init; } = null!;
    public string? PersonaId { get; init; }
    public string Expression { get; init; } = null!;
    public string ResultsJson { get; init; } = null!;
    public long Total { get; init; }
    public string? Reason { get; init; }
    public DiceVisibility Visibility { get; init; } = DiceVisibility.Public;
}

/// <summary>
/// One server-rolled expression and what it produced. It lives in Guild rather than Messaging
/// because the module permission, the persona and the channel all resolve here, and because a roll
/// has structured fields worth querying that Scylla is the wrong store for. Keyed to the message
/// Messaging returned, the same way Channel.LastMessageId already points into that service.
/// </summary>
public class DiceRoll : BaseEntity<DiceRoll>, IPrefixedEntity
{
    public static string Prefix { get; } = "roll";

    /// <summary>The message this roll was posted as, unique because a message is one roll.</summary>
    public string MessageId { get; set; } = null!;

    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>The real account, never the persona - moderation and the audit trail resolve on this.</summary>
    public string RollerUserId { get; set; } = null!;

    /// <summary>Which character rolled, or null when the roller spoke as themselves.</summary>
    public string? PersonaId { get; set; }

    /// <summary>The normalized notation, not the raw text the client sent.</summary>
    public string Expression { get; set; } = null!;

    /// <summary>Per-term dice and subtotals, as opaque JSON on the EmbedsJson precedent.</summary>
    public string ResultsJson { get; set; } = null!;

    public long Total { get; set; }

    /// <summary>What the roller said they were rolling for, free text.</summary>
    public string? Reason { get; set; }

    /// <summary>Only <see cref="DiceVisibility.Public"/> is accepted today; the column exists so
    /// nothing has to be backfilled when per-recipient visibility lands.</summary>
    public DiceVisibility Visibility { get; set; } = DiceVisibility.Public;

    /// <summary>Records a roll that has already been evaluated and posted.</summary>
    /// <param name="params">The message it was posted as, and what it produced.</param>
    /// <returns>The new record.</returns>
    public static DiceRoll Create(CreateDiceRollParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new DiceRoll
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            MessageId = @params.MessageId,
            GuildId = @params.GuildId,
            ChannelId = @params.ChannelId,
            RollerUserId = @params.RollerUserId,
            PersonaId = @params.PersonaId,
            Expression = @params.Expression,
            ResultsJson = @params.ResultsJson,
            Total = @params.Total,
            Reason = @params.Reason,
            Visibility = @params.Visibility,
        };
    }
}
