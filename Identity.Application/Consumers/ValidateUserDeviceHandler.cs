using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

public class ValidateUserDeviceHandler
{
    public static async Task<ValidateUserDeviceResponse> Handle(
        ValidateUserDeviceRequest request,
        MicroserviceContext ctx)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ClientDeviceId))
            return new ValidateUserDeviceResponse { IsRegistered = false };

        var device = await ctx.UserDevices.AsNoTracking()
            .Where(d => d.UserId == request.UserId
                        && d.ClientDeviceId == request.ClientDeviceId
                        && d.Status == DeviceStatus.Active)
            .Select(d => new { d.DeviceName })
            .FirstOrDefaultAsync();

        return new ValidateUserDeviceResponse
        {
            IsRegistered = device is not null,
            DeviceName = device?.DeviceName,
        };
    }
}
