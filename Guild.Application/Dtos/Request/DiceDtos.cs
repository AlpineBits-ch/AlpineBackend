using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>Rolls dice into a channel.</summary>
public class CreateDiceRollDto
{
    /// <summary>System-neutral notation, for example <c>4d6kh3+2</c>.</summary>
    public required string Expression { get; set; }

    /// <summary>Which character is rolling; omitted, the channel's autoproxy decides.</summary>
    public string? PersonaId { get; set; }

    /// <summary>What the roll is for, shown alongside the result.</summary>
    public string? Reason { get; set; }

    /// <summary>Only <see cref="DiceVisibility.Public"/> is accepted; the rest are refused rather
    /// than quietly downgraded.</summary>
    public DiceVisibility Visibility { get; set; } = DiceVisibility.Public;
}
