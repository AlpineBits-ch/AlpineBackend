using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

public class ConsumeMlsTokensForUserHandler
{
    public static async Task<ConsumeMlsDeviceTokensForUserResponse> Handle(ConsumeMlsDeviceTokensForUserRequest request, MicroserviceContext ctx)
    {
        var tokens = new List<UserKeyPackage>();

        var allDevices = new List<UserDevice>();

        foreach (var userId in request.UserIds)
        {
            var devices = ctx.UserDevices.Where(x => x.UserId == userId && x.Status == DeviceStatus.Active)
                .Include(userDevice => userDevice.KeyPackages).ToList();

            foreach (var device in devices)
            {
                var token = device.KeyPackages.FirstOrDefault(x => x.ConsumedAt == null);
                if (token == null)
                {
                    continue;
                }
                token.ConsumedAt = DateTime.UtcNow;
                tokens.Add(token);
                allDevices.AddRange(devices);
            }
        }

       
        
        
        return new ConsumeMlsDeviceTokensForUserResponse()
        {
            DeviceTokens = tokens.Select(t => new DeviceTokenResponse()
            {
                DeviceId = allDevices.Single(d => d.Id == t.DeviceId).ClientDeviceId,
                UserId = t.UserId,
                Token = t.KeyPackage,
            }).ToList()
        };
    }
}