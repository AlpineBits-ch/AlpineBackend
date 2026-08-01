using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Hands out exactly one MLS KeyPackage per active device of each requested user, so the caller can
/// add every one of those devices to a group.
///
/// <para>Selection order per device: the oldest unconsumed, unexpired single-use package first
/// (oldest, so the supply drains in upload order instead of leaving a stale tail behind), then that
/// device's last-resort package if it has one. A device with neither is reported in
/// <see cref="ConsumeMlsDeviceTokensForUserResponse.UnreachableDevices"/> rather than dropped on the
/// floor - from the user's side, a device quietly left out of a group is indistinguishable from the
/// conversation simply being broken.</para>
/// </summary>
public class ConsumeMlsTokensForUserHandler
{
    public static async Task<ConsumeMlsDeviceTokensForUserResponse> Handle(
        ConsumeMlsDeviceTokensForUserRequest request,
        MicroserviceContext ctx)
    {
        var now = DateTime.UtcNow;

        var userIds = request.UserIds?.Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new ConsumeMlsDeviceTokensForUserResponse();

        // One query for every requested user, and the device is carried alongside its own package
        // rather than looked up afterwards in a flat list. The previous shape appended the whole
        // per-user device list once per device that had a token, so any user with two devices
        // produced duplicates and the later `Single(...)` lookup threw.
        var devices = await ctx.UserDevices
            .Where(d => userIds.Contains(d.UserId) && d.Status == DeviceStatus.Active)
            .Include(d => d.KeyPackages)
            .ToListAsync();

        // Which of the requested accounts refuse the reusable last-resort package. Read once here
        // rather than per device, since a level is an account-wide property.
        var strictAccounts = (await ctx.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && u.ProtectionLevel == ProtectionLevel.VerifiedDevices)
                .Select(u => u.Id)
                .ToListAsync())
            .ToHashSet();

        var tokens = new List<DeviceTokenResponse>();
        var unreachable = new List<UnreachableDeviceResponse>();

        foreach (var device in devices)
        {
            // Single-use first; the last-resort package is a floor, not a substitute.
            var package = device.KeyPackages
                .Where(p => p is { IsLastResort: false, ConsumedAt: null } && p.ExpiresAt > now)
                .MinBy(p => p.CreatedAt);

            var usedLastResort = false;

            if (package is not null)
            {
                package.ConsumedAt = now;
            }
            else if (!strictAccounts.Contains(device.UserId))
            {
                // Reusable by design - see UserKeyPackage.IsLastResort. Deliberately not stamped.
                package = device.KeyPackages
                    .Where(p => p.IsLastResort && p.ExpiresAt > now)
                    .MaxBy(p => p.CreatedAt);
                usedLastResort = package is not null;
            }

            // A VerifiedDevices account has no floor to fall back to: joining through a reusable
            // package costs the leaf its forward secrecy, and refusing that is most of what the
            // strict tier is buying. So the device is reported unreachable instead - honestly
            // unreachable, since it genuinely has no single-use package left - and the user is told
            // to open it so it can replenish.
            if (package is null)
            {
                unreachable.Add(new UnreachableDeviceResponse
                {
                    UserId = device.UserId,
                    DeviceId = device.ClientDeviceId,
                    DeviceName = device.DeviceName,
                });
                continue;
            }

            tokens.Add(new DeviceTokenResponse
            {
                // The client device id, not the row id: this is what the caller addresses Welcomes
                // and per-device realtime pushes to.
                DeviceId = device.ClientDeviceId,
                UserId = device.UserId,
                Token = package.KeyPackage,
                // Carried with the package, not fetched separately: otherwise there is a window in
                // which the server could pair one device's key package with another's certificate.
                Certificate = device.HasValidCertificateAt(now) ? device.Certificate : null,
                CertificateExpiresAt = device.CertificateExpiresAt,
                CertificateIdentityKeyVersion = device.CertificateIdentityKeyVersion,
                IsLastResort = usedLastResort,
            });
        }

        return new ConsumeMlsDeviceTokensForUserResponse
        {
            DeviceTokens = tokens,
            UnreachableDevices = unreachable,
        };
    }
}
