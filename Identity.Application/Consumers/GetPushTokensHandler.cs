using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ContractKind = Identity.Contracts.Enums.PushTokenKind;
using DomainKind = Identity.Domain.Enums.PushTokenKind;

namespace Identity.Application.Consumers;

/// <summary>Single source of push endpoints for every sender.</summary>
public class GetPushTokensHandler
{
    public static async Task<GetPushTokensForUsersResponse> Handle(
        GetPushTokensForUsersRequest request,
        MicroserviceContext ctx)
    {
        var userIds = request.UserIds?.Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new GetPushTokensForUsersResponse();

        var query = ctx.UserPushTokens.AsNoTracking().Where(t => userIds.Contains(t.UserId));

        if (request.Kinds is { Count: > 0 })
        {
            var kinds = request.Kinds.Select(ToDomain).ToList();
            query = query.Where(t => kinds.Contains(t.Kind));
        }

        var rows = await query
            .Select(t => new
            {
                t.UserId,
                t.Token,
                t.Kind,
                // Null for FCM and APNs rows.
                t.P256dh,
                t.Auth,
                // Devices are addressed by the client-supplied id everywhere off this service, so
                // the row id never leaves.
                ClientDeviceId = t.Device != null ? t.Device.ClientDeviceId : null,
                // What the build behind this token can be sent.
                Capabilities = t.Device != null ? t.Device.Capabilities : null,
            })
            .ToListAsync();

        var excluded = request.ExcludeClientDeviceIds?.ToHashSet() ?? [];

        return new GetPushTokensForUsersResponse
        {
            Tokens = rows
                .Where(r => r.ClientDeviceId is null || !excluded.Contains(r.ClientDeviceId))
                .Select(r => new PushTokenResponse
                {
                    UserId = r.UserId,
                    Token = r.Token,
                    Kind = ToContract(r.Kind),
                    P256dh = r.P256dh,
                    Auth = r.Auth,
                    ClientDeviceId = r.ClientDeviceId,
                    Capabilities = r.Capabilities ?? [],
                })
                .ToList(),
        };
    }

    // Every member named explicitly, with Fcm as the fall-through only because it is the historical
    // default for an unrecognised value.
    private static DomainKind ToDomain(ContractKind kind) => kind switch
    {
        ContractKind.ApnsVoip => DomainKind.ApnsVoip,
        ContractKind.WebPush => DomainKind.WebPush,
        _ => DomainKind.Fcm,
    };

    private static ContractKind ToContract(DomainKind kind) => kind switch
    {
        DomainKind.ApnsVoip => ContractKind.ApnsVoip,
        DomainKind.WebPush => ContractKind.WebPush,
        _ => ContractKind.Fcm,
    };
}
