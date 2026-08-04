using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;

namespace Federation.Application.Commands;

/// <summary>
/// Federation's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling
/// of <see cref="PurgeUserDataCommandHandler"/>, and a no-op for the same reason that one is.
///
/// <para><c>FederatedResource</c> has no direct <c>UserId</c> field (see federation-protocol.md's
/// canonical-ID / shadow-entity model): a user is only reachable indirectly, through the Guild/Social
/// rows Federation mirrors, and every one of those is exported by the service that owns it. Producing
/// a fragment here would either duplicate those sections or invent a join that does not exist.</para>
///
/// <para>What this does <b>not</b> cover, and what a subject should be told out of band: shadow copies
/// of their memberships and friendships held by <i>remote</i> instances. Those are separately
/// administered deployments; this instance cannot enumerate them and could not vouch for the answer if
/// it could. That is the same disclosed limitation the purge carries, and it is a limitation of
/// federation rather than of this handler.</para>
///
/// <para>Still occupies a real participant slot, rather than being omitted, so that if Federation ever
/// gains directly user-owned local data, wiring it in is additive - and so the export's participant
/// list stays identical to the purge's, which is the invariant that keeps "what we erase" and "what we
/// disclose" from drifting apart.</para>
/// </summary>
public class ExportUserDataCommandHandler
{
    public static Task<ExportUserDataResponse> Handle(ExportUserDataCommand command)
    {
        var fragment = new
        {
            notice =
                "This instance holds no federation data keyed directly to your account. Your guild "
                + "memberships and friendships are exported by the guild and social sections. Copies "
                + "mirrored by other federated instances are held by those instances, not by this one.",
        };

        return Task.FromResult(new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "federation",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>(),
        });
    }
}
