using System.Text.Json;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;

namespace Guild.Application.Services;

public class AuditLogService(MicroserviceContext ctx)
{
    /// <summary>
    /// Queues an audit log entry on the current change-tracker; committed together with
    /// whatever else the caller's SaveChanges call persists, so the entry only exists if
    /// the action it describes actually took effect.
    /// </summary>
    public void Log(string guildId, string actorUserId, AuditActionType actionType, string? targetId = null, object? metadata = null)
    {
        ctx.Set<GuildAuditLogEntry>().Add(GuildAuditLogEntry.Create(new CreateAuditLogEntryParams
        {
            GuildId = guildId,
            ActorUserId = actorUserId,
            ActionType = actionType,
            TargetId = targetId,
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
        }));
    }
}
