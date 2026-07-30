using System.Security.Claims;
using System.Text.Json;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Services.Qr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Controllers;

/// <summary>
/// Pre-auth handshake for QR cross-device login: an unauthenticated desktop/web client starts a
/// pairing code and polls its status; an already-authenticated mobile client scans and approves
/// it. Approval never issues tokens directly - the desktop redeems the approved code at
/// /connect/token via ConnectController's QrLoginService.QrGrantType branch, the one audited place
/// tokens are minted.
/// </summary>
[ApiController]
[Route("api/v1/qr-login")]
public class QrLoginController(IDistributedCache cache) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("start")]
    public async Task<IActionResult> StartAsync(StartQrLoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceName)) return BadRequest("deviceName is required.");

        var code = Guid.NewGuid().ToString("N");
        var state = new QrPairingState(QrPairingStatus.Pending, dto.DeviceName, dto.DeviceType, null);

        await cache.SetStringAsync(QrLoginService.PairingCacheKey(code), JsonSerializer.Serialize(state),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = QrLoginService.PairingLifetime });

        return Ok(new QrLoginStartedDto
        {
            Code = code,
            ExpiresInSeconds = (int)QrLoginService.PairingLifetime.TotalSeconds,
        });
    }

    [AllowAnonymous]
    [HttpGet("status/{code}")]
    public async Task<IActionResult> GetStatusAsync(string code)
    {
        var state = await GetStateAsync(code);
        if (state is null) return NotFound();

        return Ok(new QrLoginStatusDto { Status = state.Status });
    }

    [Authorize]
    [HttpPost("scan")]
    public async Task<IActionResult> ScanAsync(QrLoginCodeDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var cacheKey = QrLoginService.PairingCacheKey(dto.Code);
        var state = await GetStateAsync(dto.Code);
        if (state is null || state.Status != QrPairingStatus.Pending) return NotFound();

        // The scanning user is recorded now (not deferred to approve) so approve can be
        // restricted to the same identity that scanned - prevents a second, unrelated
        // authenticated session from approving/denying a code it never scanned.
        var scanned = state with { Status = QrPairingStatus.Scanned, UserId = userId };
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(scanned),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = QrLoginService.PairingLifetime });

        return Ok(new QrLoginDeviceInfoDto { DeviceName = state.DeviceName, DeviceType = state.DeviceType });
    }

    [Authorize]
    [HttpPost("approve")]
    public async Task<IActionResult> ApproveAsync(ApproveQrLoginDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var cacheKey = QrLoginService.PairingCacheKey(dto.Code);
        var state = await GetStateAsync(dto.Code);
        if (state is null || state.Status != QrPairingStatus.Scanned) return NotFound();
        if (state.UserId != userId) return Forbid();

        var decided = state with { Status = dto.Approve ? QrPairingStatus.Approved : QrPairingStatus.Denied };
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(decided),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = QrLoginService.PairingLifetime });

        return NoContent();
    }

    private async Task<QrPairingState?> GetStateAsync(string code)
    {
        var json = await cache.GetStringAsync(QrLoginService.PairingCacheKey(code));
        return json is null ? null : JsonSerializer.Deserialize<QrPairingState>(json);
    }
}
