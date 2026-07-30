using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateDecisionParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string CreatedByUserId { get; set; } = null!;
    public DateTimeOffset? ClosesAt { get; set; }
    public int? Quorum { get; set; }
}

/// <summary>
/// A house decision resolved by consent rather than majority: an option wins when quorum is met and
/// nobody has blocked it.
/// </summary>
public class Decision : BaseEntity<Decision>, IPrefixedEntity
{
    public static string Prefix { get; } = "decn";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string CreatedByUserId { get; set; } = null!;

    /// <summary>Null means open until someone closes it explicitly.</summary>
    public DateTimeOffset? ClosesAt { get; set; }

    /// <summary>Minimum number of non-abstain votes before the decision can resolve.</summary>
    public int? Quorum { get; set; }

    public DecisionStatus Status { get; set; } = DecisionStatus.Open;

    /// <summary>Set when Status becomes Decided.</summary>
    public string? OutcomeOptionId { get; set; }

    public virtual ICollection<DecisionOption> Options { get; set; } = [];
    public virtual ICollection<DecisionVote> Votes { get; set; } = [];

    public static Decision Create(CreateDecisionParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new Decision
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Title = @params.Title,
            Description = @params.Description,
            CreatedByUserId = @params.CreatedByUserId,
            ClosesAt = @params.ClosesAt,
            Quorum = @params.Quorum,
        };
    }

    /// <summary>Resolves the decision from its current votes without mutating it.</summary>
    public (DecisionStatus Status, string? WinningOptionId) Resolve()
    {
        var participating = Votes.Count(v => v.Kind != DecisionVoteKind.Abstain);
        if (Quorum is not null && participating < Quorum) return (DecisionStatus.Expired, null);
        if (participating == 0) return (DecisionStatus.Expired, null);

        var blockedOptionIds = Votes
            .Where(v => v.Kind == DecisionVoteKind.Block && v.OptionId is not null)
            .Select(v => v.OptionId!)
            .ToHashSet();

        // A block with no OptionId blocks the whole decision - "none of these, and here's why".
        if (Votes.Any(v => v.Kind == DecisionVoteKind.Block && v.OptionId is null))
            return (DecisionStatus.Blocked, null);

        var eligible = Options
            .Where(o => !blockedOptionIds.Contains(o.Id))
            .OrderByDescending(o => Votes.Count(v => v.Kind == DecisionVoteKind.Support && v.OptionId == o.Id))
            .ThenBy(o => o.Position)
            .ToList();

        if (eligible.Count == 0) return (DecisionStatus.Blocked, null);

        var winner = eligible[0];
        var winnerSupport = Votes.Count(v => v.Kind == DecisionVoteKind.Support && v.OptionId == winner.Id);

        // Every remaining option has zero support: quorum was met by blocks on other options
        // only, so there is no positive result to record.
        return winnerSupport == 0
            ? (DecisionStatus.Blocked, null)
            : (DecisionStatus.Decided, winner.Id);
    }
}

public class DecisionOption : BaseEntity<DecisionOption>, IPrefixedEntity
{
    public static string Prefix { get; } = "dcop";

    public string DecisionId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public int Position { get; set; }
}

/// <summary>One member's position on a decision.</summary>
public class DecisionVote
{
    public string DecisionId { get; set; } = null!;
    public string UserId { get; set; } = null!;

    /// <summary>Which option.</summary>
    public string? OptionId { get; set; }

    public DecisionVoteKind Kind { get; set; }

    /// <summary>Required for Block, ignored otherwise.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
