using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;

namespace Federation.Application.Commands;

/// <summary>
/// Federation's participant in the AccountDeletionSaga fan-out. Intentionally a no-op today:
/// FederatedResource has no direct UserId field (see federation-protocol.md's canonical-ID /
/// shadow-entity model) - a deleted user is only reachable indirectly, through the Guild/Social
/// rows Federation mirrors, and those are purged independently by their own handlers.
///
/// What this does NOT do: notify remote federation instances that mirrored this user's guild
/// memberships/friendships so they can purge their own shadow copies. That requires the full
/// outward retraction/tombstone leg of the federation protocol, which is real, separate work -
/// tracked as a known follow-up, not solved here. Compliance on the remote side is best-effort
/// regardless (it's a separately administered deployment), so this is disclosed as a limitation
/// rather than something this fan-out can guarantee.
///
/// Still occupies a real participant slot in the saga (rather than being omitted) so that if
/// Federation ever gains directly user-owned local data, wiring it in is additive.
/// </summary>
public class PurgeUserDataCommandHandler
{
    public static Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command)
    {
        return Task.FromResult(new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "federation",
        });
    }
}
