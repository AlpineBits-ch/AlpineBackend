using Persistence;

namespace Guild.Domain.Entity;

public class CreateMemberAbsenceParams
{
    public string GuildId { get; set; } = null!;
    public string UserId { get; set; } = null!;
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public string? Note { get; set; }
    public string CreatedByUserId { get; set; } = null!;
}

/// <summary>
/// A stretch of time a member will not be in the house - a holiday, a work trip, a month at a
/// parent's.
/// </summary>
public class MemberAbsence : BaseEntity<MemberAbsence>, IPrefixedEntity
{
    public static string Prefix { get; } = "absn";

    public string GuildId { get; set; } = null!;

    /// <summary>Whose absence this is. Only they may create one - see AbsenceEndpoint.</summary>
    public string UserId { get; set; } = null!;

    public DateTimeOffset StartAt { get; set; }

    /// <summary>Exclusive.</summary>
    public DateTimeOffset EndAt { get; set; }

    /// <summary>"In Lisbon". Free text, shown on the board so the house can plan around it.</summary>
    public string? Note { get; set; }

    /// <summary>Today this is always <see cref="UserId"/>, because only the member themselves may
    /// declare an absence. Stored anyway so that "Anna is on a plane and forgot to add it" can be
    /// entered by somebody else later without a migration, and so the answer to who entered a row
    /// is recorded rather than inferred.</summary>
    public string CreatedByUserId { get; set; } = null!;

    public static MemberAbsence Create(CreateMemberAbsenceParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new MemberAbsence
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = @params.GuildId,
            UserId = @params.UserId,
            StartAt = @params.StartAt,
            EndAt = @params.EndAt,
            Note = @params.Note,
            CreatedByUserId = @params.CreatedByUserId,
        };
    }

    /// <summary>Whether this absence is in effect at <paramref name="instant"/>.</summary>
    public bool Covers(DateTimeOffset instant) => StartAt <= instant && instant < EndAt;

    /// <summary>Whether this absence shares any time at all with [<paramref name="start"/>,
    /// <paramref name="end"/>). Touching at an endpoint is not an overlap, for the same reason
    /// <see cref="EndAt"/> is exclusive.</summary>
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartAt < end && start < EndAt;

    /// <summary>
    /// How many days of [<paramref name="from"/>, <paramref name="to"/>) the given absences cover
    /// between them.
    /// </summary>
    public static double AbsentDaysWithin(
        IEnumerable<MemberAbsence> absences, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;

        var clamped = absences
            .Select(a => (Start: a.StartAt < from ? from : a.StartAt, End: a.EndAt > to ? to : a.EndAt))
            .Where(a => a.End > a.Start)
            .OrderBy(a => a.Start)
            .ToList();

        if (clamped.Count == 0) return 0;

        var total = TimeSpan.Zero;
        var (currentStart, currentEnd) = clamped[0];

        foreach (var (start, end) in clamped.Skip(1))
        {
            if (start <= currentEnd)
            {
                if (end > currentEnd) currentEnd = end;
                continue;
            }

            total += currentEnd - currentStart;
            (currentStart, currentEnd) = (start, end);
        }

        total += currentEnd - currentStart;

        return total.TotalDays;
    }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<MemberAbsence>(absenceBuilder =>
// {
//     absenceBuilder.HasOne<Domain.Aggregates.Guild>()
//         .WithMany()
//         .HasForeignKey(x => x.GuildId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // The rotation's question: who in this guild is away at a given instant.
//     absenceBuilder.HasIndex(x => new { x.GuildId, x.StartAt, x.EndAt });
//
//     // The board's question, and the per-member overlap and count guards on write.
//     absenceBuilder.HasIndex(x => new { x.GuildId, x.UserId, x.EndAt });
// });
// DbSet: public DbSet<MemberAbsence> MemberAbsences { get; set; }
// MapEnum: none - this entity has no enum columns.
