using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;

namespace Import.Application.Commands;

/// <summary>
/// Import's participant in the AccountDeletionSaga fan-out. Intentionally a no-op today:
/// ImportJob.RequestedByUserId is attribution-only (who kicked off a one-time Discord import),
/// the same shape as Guild.GuildAuditLogEntry.ActorUserId or Guild.WikiRevision.AuthorId - it
/// isn't rendered as "your" data the way a Profile or GuildMember row is, so per the same
/// reasoning applied there it's left pointing at the now-tombstoned Identity user rather than
/// rewritten. GuildLink/ImportEntityMapping carry no user reference at all.
///
/// Still occupies a real participant slot in the saga so that if Import ever gains real
/// user-owned data, wiring it in is additive rather than a new fan-out target to invent.
/// </summary>
public class PurgeUserDataCommandHandler
{
    public static Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command)
    {
        return Task.FromResult(new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "import",
        });
    }
}
