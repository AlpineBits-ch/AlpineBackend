using System.Security.Claims;
using Facet.Extensions;
using Identity.Application.Dtos.Response;
using Identity.Domain.Entities;
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
    /// <summary>How many usable single-use key packages a device should keep on the server. Each
    /// group this device is added to consumes exactly one.</summary>
    public const int TargetKeyPackageCount = 100;

    /// <summary>How long a consumed package is kept before being swept - long enough to debug a
    /// failed join against, short enough that the table does not grow without bound.</summary>
    public static readonly TimeSpan ConsumedRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// How many key packages this device should generate and upload to get back to <see
    /// cref="TargetKeyPackageCount"/>.
    /// </summary>
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

        var now = DateTime.UtcNow;

        var sweepBefore = now - ConsumedRetention;
        var dead = await ctx.UserKeyPackages
            .Where(p => p.DeviceId == device.Id
                        && ((p.ConsumedAt != null && p.ConsumedAt < sweepBefore) || p.ExpiresAt <= now))
            .ToListAsync();
        if (dead.Count > 0)
        {
            ctx.UserKeyPackages.RemoveRange(dead);
            await ctx.SaveChangesAsync();
        }

        // Only unexpired single-use packages count toward the target.
        var usable = await ctx.UserKeyPackages
            .CountAsync(p => p.DeviceId == device.Id
                             && p.ConsumedAt == null
                             && !p.IsLastResort
                             && p.ExpiresAt > now);

        var hasLastResort = await ctx.UserKeyPackages
            .AnyAsync(p => p.DeviceId == device.Id && p.IsLastResort && p.ExpiresAt > now);

        return Ok(new GenerateKeyPackagesDto
        {
            Count = Math.Clamp(TargetKeyPackageCount - usable, min: 0, max: TargetKeyPackageCount),
            NeedsLastResort = !hasLastResort,
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetDevicesAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var devices = await ctx.UserDevices.AsNoTracking().Where(x => x.UserId == userId).ToListAsync();

        return Ok(devices.SelectFacets<UserDevice, UserDeviceDto>());
    }
}
