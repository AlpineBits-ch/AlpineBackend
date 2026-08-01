using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>Lists the active devices of a set of users, without consuming anything.</summary>
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
