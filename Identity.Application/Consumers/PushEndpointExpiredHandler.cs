using Identity.Contracts.Bus.Events;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ContractKind = Identity.Contracts.Enums.PushTokenKind;
using DomainKind = Identity.Domain.Enums.PushTokenKind;

namespace Identity.Application.Consumers;

/// <summary>
/// Deletes a push endpoint a push service has told us is gone (RFC 8030: 404 or 410).
/// </summary>
public class PushEndpointExpiredHandler
{
    public static async Task Handle(
        PushEndpointExpiredEvent expired,
        MicroserviceContext ctx,
        ILogger<PushEndpointExpiredHandler> logger)
    {
        if (string.IsNullOrWhiteSpace(expired.Token)) return;

        var kind = ToDomain(expired.Kind);

        var rows = await ctx.UserPushTokens
            .Where(t => t.Kind == kind && t.Token == expired.Token)
            .ToListAsync();

        if (rows.Count == 0) return;

        ctx.UserPushTokens.RemoveRange(rows);

        // The endpoint itself is not logged.
        logger.LogInformation(
            "Removed {Count} expired {Kind} push endpoint(s) for user(s) {UserIds}",
            rows.Count, kind, string.Join(",", rows.Select(r => r.UserId).Distinct()));
    }

    private static DomainKind ToDomain(ContractKind kind) => kind switch
    {
        ContractKind.ApnsVoip => DomainKind.ApnsVoip,
        ContractKind.WebPush => DomainKind.WebPush,
        _ => DomainKind.Fcm,
    };
}
