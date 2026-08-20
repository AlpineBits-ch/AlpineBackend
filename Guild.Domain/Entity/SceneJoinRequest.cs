using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateSceneJoinRequestParams
{
    public string SceneChannelId { get; init; } = null!;
    public string GuildId { get; init; } = null!;
    public string PersonaId { get; init; } = null!;
    public string RequestedByUserId { get; init; } = null!;
    public string? Note { get; init; }
}

/// <summary>
/// One ask to bring a character into a scene whose policy is <see cref="SceneJoinPolicy.Ask"/>.
/// Decided rows stay: the player's inbox reads the reason off them, and asking again is a new row
/// rather than a reopened one.
/// </summary>
public class SceneJoinRequest : BaseEntity<SceneJoinRequest>, IPrefixedEntity
{
    public static string Prefix { get; } = "scjr";

    public string SceneChannelId { get; set; } = null!;

    public string GuildId { get; set; } = null!;

    public string PersonaId { get; set; } = null!;

    public string RequestedByUserId { get; set; } = null!;

    /// <summary>The player's pitch, which the GM's banner shows under the character.</summary>
    public string? Note { get; set; }

    public SceneJoinRequestStatus Status { get; set; } = SceneJoinRequestStatus.Pending;

    public string? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Why a GM said no, shown to the player and to nobody else.</summary>
    public string? DecisionReason { get; set; }

    public const int MaxNoteLength = 300;

    public const int MaxReasonLength = 300;

    public static SceneJoinRequest Create(CreateSceneJoinRequestParams parameters)
    {
        var date = DateTimeOffset.UtcNow;

        return new SceneJoinRequest
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            SceneChannelId = parameters.SceneChannelId,
            GuildId = parameters.GuildId,
            PersonaId = parameters.PersonaId,
            RequestedByUserId = parameters.RequestedByUserId,
            Note = Trim(parameters.Note, MaxNoteLength),
        };
    }

    /// <summary>Records a GM's answer.</summary>
    /// <param name="status">Approved or Denied.</param>
    /// <param name="decidedByUserId">Who answered it.</param>
    /// <param name="reason">The reason a denial carries, trimmed to the column's limit.</param>
    /// <param name="now">The instant it was answered.</param>
    public void Decide(
        SceneJoinRequestStatus status, string decidedByUserId, string? reason, DateTimeOffset now)
    {
        Status = status;
        DecidedByUserId = decidedByUserId;
        DecidedAt = now;
        DecisionReason = Trim(reason, MaxReasonLength);
        UpdatedAt = now;
    }

    /// <summary>Takes the ask back, which only the player who made it may do.</summary>
    /// <param name="now">The instant it was withdrawn.</param>
    public void Withdraw(DateTimeOffset now)
    {
        Status = SceneJoinRequestStatus.Withdrawn;
        UpdatedAt = now;
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<SceneJoinRequest>(requestBuilder =>
// {
//     requestBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithMany()
//         .HasForeignKey(x => x.SceneChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     requestBuilder.HasIndex(x => new { x.GuildId, x.Status });
//     requestBuilder.HasIndex(x => new { x.SceneChannelId, x.Status });
//
//     requestBuilder.HasIndex(x => new { x.SceneChannelId, x.PersonaId })
//         .IsUnique()
//         .HasFilter("status = 'pending'");
// });
// DbSet: public DbSet<SceneJoinRequest> SceneJoinRequests { get; set; }
// MapEnum: options.MapEnum<SceneJoinRequestStatus>();
