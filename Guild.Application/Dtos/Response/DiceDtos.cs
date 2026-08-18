using System.Text.Json.Serialization;
using Guild.Domain.Enums;
using Guild.Domain.Services;

namespace Guild.Application.Dtos.Response;

/// <summary>What one term of an expression produced.</summary>
public class DiceRollTermDto
{
    /// <summary>The term as written back, for example <c>4d6kh3</c>.</summary>
    public string Notation { get; set; } = null!;

    /// <summary>1 or -1.</summary>
    public int Sign { get; set; }

    /// <summary>Set for a constant term, null for a dice pool.</summary>
    public int? Constant { get; set; }

    /// <summary>One entry per die, each already carrying whatever it exploded into.</summary>
    public List<int> Dice { get; set; } = [];

    /// <summary>The dice a keep or drop mode let count.</summary>
    public List<int> Kept { get; set; } = [];

    /// <summary>The term's contribution before its sign is applied.</summary>
    public long Subtotal { get; set; }

    public static DiceRollTermDto From(DiceTermResult result) => new()
    {
        Notation = result.Notation,
        Sign = result.Sign,
        Constant = result.Constant,
        Dice = [..result.Dice],
        Kept = [..result.Kept],
        Subtotal = result.Subtotal,
    };
}

/// <summary>A server-rolled expression and the message it was posted as.</summary>
public class DiceRollDto
{
    public string Id { get; set; } = null!;
    public string MessageId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>The real account that rolled, whatever character it was rolled as.</summary>
    public string RollerUserId { get; set; } = null!;

    public string? PersonaId { get; set; }

    /// <summary>The normalized notation, not the raw text that was sent.</summary>
    public string Expression { get; set; } = null!;

    public string? Reason { get; set; }
    public long Total { get; set; }
    public DiceVisibility Visibility { get; set; }

    /// <summary>The same plain-text rendering the message content carries.</summary>
    public string Breakdown { get; set; } = null!;

    public List<DiceRollTermDto> Terms { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The structured half of a roll, serialized into the message's embed and into
/// <c>DiceRoll.ResultsJson</c> so a rich client and a stored row agree on one shape.
/// </summary>
public class DiceRollResultsJson
{
    [JsonPropertyName("expression")] public string Expression { get; set; } = null!;
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("breakdown")] public string Breakdown { get; set; } = null!;
    [JsonPropertyName("terms")] public List<DiceRollTermDto> Terms { get; set; } = [];
}
