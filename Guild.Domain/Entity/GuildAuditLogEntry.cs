using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateAuditLogEntryParams
{
    public string GuildId { get; set; } = null!;
    public string ActorUserId { get; set; } = null!;
    public AuditActionType ActionType { get; set; }
    public string? TargetId { get; set; }
    public string? Metadata { get; set; }
}

public class GuildAuditLogEntry : BaseEntity<GuildAuditLogEntry>, IPrefixedEntity
{
    public static string Prefix { get; } = "adlg";

    public string GuildId { get; set; } = null!;
    public Aggregates.Guild Guild { get; set; } = null!;

    public string ActorUserId { get; set; } = null!;
    public AuditActionType ActionType { get; set; }

    /// <summary>Id of the entity/user this action targeted, e.g. the banned user id or role id.</summary>
    public string? TargetId { get; set; }

    /// <summary>Small JSON blob of free-form details (e.g. ban reason, old/new values).</summary>
    public string? Metadata { get; set; }

    public static GuildAuditLogEntry Create(CreateAuditLogEntryParams parameters)
    {
        
        var date = DateTime.UtcNow;
        return new GuildAuditLogEntry
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = parameters.GuildId,
            ActorUserId = parameters.ActorUserId,
            ActionType = parameters.ActionType,
            TargetId = parameters.TargetId,
            Metadata = parameters.Metadata,
        };
    }
}
