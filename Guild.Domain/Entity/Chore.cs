using Persistence;

namespace Guild.Domain.Entity;

public class CreateChoreParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public int IntervalDays { get; set; } = 7;
    public DateTimeOffset AnchorAt { get; set; }
    public int EffortMinutes { get; set; } = 15;
    public string? RotationRoleId { get; set; }
    public string? FixedAssigneeUserId { get; set; }
    public int GraceHours { get; set; } = 24;
}

/// <summary>A recurring household task.</summary>
public class Chore : BaseEntity<Chore>, IPrefixedEntity
{
    public static string Prefix { get; } = "chor";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Plain interval rather than an RRULE.</summary>
    public int IntervalDays { get; set; } = 7;

    /// <summary>The first due date.</summary>
    public DateTimeOffset AnchorAt { get; set; }

    /// <summary>The fairness weight: roughly how long this takes.</summary>
    public int EffortMinutes { get; set; } = 15;

    /// <summary>The rotation pool. Null means this chore is always <see cref="FixedAssigneeUserId"/>'s.</summary>
    public string? RotationRoleId { get; set; }

    public string? FixedAssigneeUserId { get; set; }

    /// <summary>How long after DueAt an occurrence stays "due" before the client should show it
    /// as overdue.</summary>
    public int GraceHours { get; set; } = 24;

    public bool IsPaused { get; set; }

    /// <summary>When the next occurrence is due.</summary>
    public DateTimeOffset NextDueAt { get; set; }

    public static Chore Create(CreateChoreParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new Chore
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Title = @params.Title,
            Description = @params.Description,
            IntervalDays = @params.IntervalDays,
            AnchorAt = @params.AnchorAt,
            EffortMinutes = @params.EffortMinutes,
            RotationRoleId = @params.RotationRoleId,
            FixedAssigneeUserId = @params.FixedAssigneeUserId,
            GraceHours = @params.GraceHours,
            NextDueAt = @params.AnchorAt,
        };
    }

    /// <summary>Advances to the first scheduled slot strictly after <paramref name="after"/>,
    /// staying on the anchor's cadence. Stepping from the anchor rather than from "now" is what
    /// stops a chore completed three days late from permanently shifting bin day.</summary>
    public DateTimeOffset AdvanceFrom(DateTimeOffset after)
    {
        var interval = TimeSpan.FromDays(Math.Max(1, IntervalDays));
        var next = NextDueAt;

        // Bounded rather than while(true): a chore paused for years shouldn't spin here.
        for (var i = 0; i < 4096 && next <= after; i++) next += interval;

        return next;
    }

    /// <summary>
    /// Drops <see cref="NextDueAt"/> forward to the most recent slot at or before <paramref
    /// name="now"/>, and reports how many slots were passed over.
    /// </summary>
    public int FastForwardTo(DateTimeOffset now)
    {
        var interval = TimeSpan.FromDays(Math.Max(1, IntervalDays));

        var skipped = 0;
        while (skipped < 4096 && NextDueAt + interval <= now)
        {
            NextDueAt += interval;
            skipped++;
        }

        return skipped;
    }
}

/// <summary>One generated instance of a <see cref="Chore"/> - the row a member actually ticks
/// off. Kept as history after completion; the fairness balance is computed from these.</summary>
public class ChoreOccurrence : BaseEntity<ChoreOccurrence>, IPrefixedEntity
{
    public static string Prefix { get; } = "choc";

    public string ChoreId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>Denormalized from the parent chore so the board can be queried per channel
    /// without a join.</summary>
    public string ChannelId { get; set; } = null!;

    public DateTimeOffset DueAt { get; set; }
    public string AssignedUserId { get; set; } = null!;

    /// <summary>Snapshot of Chore.EffortMinutes at generation time, so re-weighting a chore
    /// doesn't silently rewrite everyone's historical balance.</summary>
    public int EffortMinutes { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedByUserId { get; set; }
    public DateTimeOffset? SkippedAt { get; set; }

    /// <summary>
    /// When the assignee was reminded this is due, or null if they have not been.
    /// </summary>
    public DateTimeOffset? RemindedAt { get; set; }

    public static ChoreOccurrence Create(Chore chore, DateTimeOffset dueAt, string assignedUserId)
    {
        var date = DateTimeOffset.UtcNow;
        return new ChoreOccurrence
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChoreId = chore.Id,
            GuildId = chore.GuildId,
            ChannelId = chore.ChannelId,
            DueAt = dueAt,
            AssignedUserId = assignedUserId,
            EffortMinutes = chore.EffortMinutes,
        };
    }
}
