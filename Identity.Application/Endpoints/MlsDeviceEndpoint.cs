using System.Security.Claims;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using FirebaseAdmin.Auth;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Domain.Entities;
using Identity.Domain.Events.Device;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Server;
using Wolverine;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

public class MlsDeviceEndpoint
{
    [Authorize]
    [WolverinePost("api/v1/devices")]
    public async Task<(IResult, DeviceRegistered?)> CreateDevice(CreateMLSDeviceDto dto,[NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);
        
        
        var existingDevice = await ctx.UserDevices.FirstOrDefaultAsync(x => x.ClientDeviceId == dto.ClientDeviceId && x.UserId == userId);
        
        if(existingDevice is not null) return (Results.Ok(existingDevice.ToFacet<UserDevice, UserDeviceDto>()), null);

        var device = UserDevice.Create(new CreateUserDeviceParams()
        {
            UserId = userId,
            ClientDeviceId = dto.ClientDeviceId,
            DeviceName = dto.DeviceName,
            DeviceType = dto.DeviceType,
            IdentityPublicKey = dto.IdentityPublicKey,
        });
        ctx.UserDevices.Add(device);
        
        return (Results.Ok(device.ToFacet<UserDevice, UserDeviceDto>()), new DeviceRegistered()
        {
            DeviceId = device.Id,
            DeviceName = device.DeviceName,
            UserId = device.UserId,
        });
    }
    
    [Authorize]
    [WolverineGet("api/v1/devices")]
    public async Task<IResult> GetDevices([NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        return Results.Ok(ctx.UserDevices.Where(x => x.UserId == userId).ToFacetsAsync<UserDevice, UserDeviceDto>());
    }
    
    [Authorize]
    [WolverinePost("api/v1/devices/client/{deviceId}/key-packages")]
    public async Task<IResult> AddKeyPackagesForDeviceAsync(string deviceId,  ClaimsPrincipal user, [FromBody] AddMLSDeviceKeyPackagesDto dto, [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var device = await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientDeviceId == deviceId && x.UserId == userId);
        if(device is null) return Results.NotFound();
        
        var packages = dto.KeyPackages.Select(p => UserKeyPackage.Create(new CreateUserKeyPackageParams()
        {
            UserId = userId,
            DeviceId = device.Id,
            KeyPackage = p.KeyPackage
        })).ToList();
        
        ctx.UserKeyPackages.AddRange(packages);
        
        return Results.Ok();
    }
    
    [Authorize]
    [WolverineGet("api/v1/devices/{deviceId}/key-packages")]
    public async Task<IResult> GetKeyPackagesForDeviceAsync( ClaimsPrincipal user, string deviceId, [NotBody] MicroserviceContext ctx)
    {
        
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        
        // verify user has access to device

        if (await ctx.UserDevices.FirstOrDefaultAsync(x => x.Id == deviceId && x.UserId == userId) is null)
        {
            return Results.Forbid();
        }

        return Results.Ok(ctx.UserKeyPackages.Where(x => x.DeviceId == deviceId && x.ConsumedAt == null).ToFacetsAsync<UserKeyPackage, UserKeyPackageDto>());
    }
}