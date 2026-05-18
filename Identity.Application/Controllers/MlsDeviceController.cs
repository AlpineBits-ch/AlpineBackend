using System.Security.Claims;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/devices")]
public class MlsDeviceController(MicroserviceContext ctx) : ControllerBase
{
    [HttpGet("client/{deviceId}/generate")]
    public async Task<IActionResult> GetGenerateTokenCommandAsync(string deviceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        
        // verify user has access to device

        var device = await ctx.UserDevices.AsNoTracking().FirstOrDefaultAsync(x => x.ClientDeviceId == deviceId && x.UserId == userId);
        
        if (device is null)
        {
            return Forbid();
        }
        
        
        var unconsumedKeyCount = await ctx.UserKeyPackages.CountAsync(p => p.DeviceId == device.Id && p.ConsumedAt == null);
        
        
        // We always want 100 Keys to be persisted, so well calculate 100-unconsumedKeys = keyToBeGenerated
        
        
        
        return Ok(new GenerateKeyPackagesDto()
        {
            Count = Math.Clamp(100 - unconsumedKeyCount, min: 0, max: 100)
        });
    }
}