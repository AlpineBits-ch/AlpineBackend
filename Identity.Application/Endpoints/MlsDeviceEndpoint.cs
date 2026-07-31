using System.Security.Claims;
using Facet.Extensions;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Domain.Entities;
using Identity.Domain.Events.Device;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

public class MlsDeviceEndpoint
{
    /// <summary>Ceiling on a single upload. The replenish flow never asks for more than the target
    /// count in one go, so anything past this is a client bug or an attempt to fill the table.</summary>
    public const int MaxKeyPackagesPerUpload = 200;

    [Authorize]
    [WolverinePost("api/v1/devices")]
    public async Task<(IResult, DeviceRegistered?)> CreateDevice(CreateMLSDeviceDto dto,[NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);


        var existingDevice = await ctx.UserDevices.FirstOrDefaultAsync(x => x.ClientDeviceId == dto.ClientDeviceId && x.UserId == userId);



        // check if device is registered by another user
        var existingDeviceByAnotherUser = await ctx.UserDevices.FirstOrDefaultAsync(x => x.ClientDeviceId == dto.ClientDeviceId && x.UserId != userId);
        if (existingDeviceByAnotherUser is not null)
        {
            // for now we just delete the existing device
            ctx.UserDevices.Remove(existingDeviceByAnotherUser);
        }


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

    // GET api/v1/devices lives on MlsDeviceController. A second registration of the same route used
    // to sit here, which additionally returned a non-awaited Task inside Results.Ok - serializing
    // the task rather than the devices.

    [Authorize]
    [WolverinePost("api/v1/devices/client/{deviceId}/key-packages")]
    public async Task<IResult> AddKeyPackagesForDeviceAsync(string deviceId, [FromBody] AddMLSDeviceKeyPackagesDto dto, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var device = await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientDeviceId == deviceId && x.UserId == userId);
        if(device is null) return Results.NotFound();

        var incoming = dto.KeyPackages ?? [];

        // An empty upload is a success, not a client error. The replenish flow asks how many
        // packages to generate and posts the result unconditionally, so a device that is already
        // fully stocked posts an empty list on every launch - rejecting that would fail a perfectly
        // correct client doing exactly what it was told.
        if (incoming.Count == 0) return Results.Ok(new AddKeyPackagesResultDto { Added = 0 });

        if (incoming.Count > MaxKeyPackagesPerUpload)
            return Results.BadRequest($"At most {MaxKeyPackagesPerUpload} key packages per upload");

        List<UserKeyPackage> packages;
        try
        {
            packages = incoming.Select(p => UserKeyPackage.Create(new CreateUserKeyPackageParams()
            {
                UserId = userId,
                DeviceId = device.Id,
                KeyPackage = p.KeyPackage,
                ExpiresAt = p.ExpiresAt,
                IsLastResort = p.IsLastResort,
            })).ToList();
        }
        catch (ArgumentException ex)
        {
            // GetCipherSuite rejects anything that is not a well-formed KeyPackage header. Accepting
            // a malformed package here just moves the failure into someone else's add_members call,
            // where it is far harder to attribute.
            return Results.BadRequest(ex.Message);
        }

        // A device keeps exactly one last-resort package; a new one supersedes the old rather than
        // accumulating, which keeps the reuse window bounded to the newest key.
        if (packages.Any(p => p.IsLastResort))
        {
            var superseded = await ctx.UserKeyPackages
                .Where(p => p.DeviceId == device.Id && p.IsLastResort)
                .ToListAsync();
            ctx.UserKeyPackages.RemoveRange(superseded);
        }

        ctx.UserKeyPackages.AddRange(packages);

        return Results.Ok(new AddKeyPackagesResultDto { Added = packages.Count });
    }

    // GET api/v1/devices/{deviceId}/key-packages is gone. It handed back the device's unconsumed
    // key packages in full without consuming them, so anything holding the user's token could
    // enumerate the supply. Clients only ever needed the *count*, which
    // MlsDeviceController.GetGenerateTokenCommandAsync already returns.
}
