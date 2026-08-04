using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;

namespace Federation.Application.Commands;

/// <summary>
/// Federation's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling
/// of <see cref="PurgeUserDataCommandHandler"/>, and a no-op for the same reason that one is.
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
