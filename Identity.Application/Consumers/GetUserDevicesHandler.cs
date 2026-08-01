using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Lists the active devices of a set of users, without consuming anything.
///
/// <para>Until this existed the only way to enumerate a user's devices was
/// <see cref="ConsumeMlsTokensForUserHandler"/>, which burns a single-use key package per device per
/// call - so asking "who is out there" cost the thing you were asking about. That is why the
/// conversation-creation reachability check was per user: per device was not answerable without
/// destroying supply.</para>
/// </summary>
public class GetUserDevicesHandler
{
    public static async Task<GetUserDevicesResponse> Handle(
        GetUserDevicesRequest request,
        MicroserviceContext ctx)
    {
        var userIds = request.UserIds?.Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new GetUserDevicesResponse();

        var now = DateTimeOffset.UtcNow;

        var devices = await ctx.UserDevices.AsNoTracking()
            .Where(d => userIds.Contains(d.UserId) && d.Status == DeviceStatus.Active)
            .Select(d => new UserDeviceSummaryResponse
            {
                UserId = d.UserId,
                ClientDeviceId = d.ClientDeviceId,
                DeviceName = d.DeviceName,
                HasValidCertificate = d.Certificate != null && d.CertificateExpiresAt > now,
                LastSeen = d.LastSeen,
            })
            .ToListAsync();

        return new GetUserDevicesResponse { Devices = devices };
    }
}
