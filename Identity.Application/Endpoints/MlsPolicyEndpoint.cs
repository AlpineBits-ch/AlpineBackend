using System.Security.Claims;
using Domain;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>
/// The rollout kill-switch for certificate enforcement, and the coverage data the decision to flip
/// it should be made on.
/// </summary>
[Authorize]
public class MlsPolicyEndpoint
{
    /// <summary>
    /// Where enforcement currently sits, plus the floor a client must meet before the tightened
    /// contracts (required <c>deviceId</c> on the Welcome fetch, rejecting creation on unreachable
    /// devices) apply to it.
    /// </summary>
    // Not "api/v1/identity/mls-policy".
    [WolverineGet("api/v1/mls-policy")]
    public static IResult Get() => Results.Ok(new MlsPolicyDto
    {
        CertificateEnforcement = MlsPolicy.CertificateEnforcement,
        MinClientVersion = MlsPolicy.MinClientVersion,
        RequireDeviceIdOnWelcomeFetch = MlsPolicy.RequireDeviceIdOnWelcomeFetch,
        RejectUnreachableDevicesOnCreate = MlsPolicy.RejectUnreachableDevicesOnCreate,
    });

    /// <summary>
    /// What fraction of active devices carry an unexpired certificate, and how many accounts have
    /// an identity key at all.
    /// </summary>
    [WolverineGet("api/v1/admin/mls-certificate-coverage")]
    public static async Task<IResult> Coverage(
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAdmin = await ctx.Users.AnyAsync(u => u.Id == userId && u.UserType == UserType.Admin);
        if (!isAdmin) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;

        var active = await ctx.UserDevices.CountAsync(d => d.Status == DeviceStatus.Active);
        var certified = await ctx.UserDevices.CountAsync(d =>
            d.Status == DeviceStatus.Active && d.Certificate != null && d.CertificateExpiresAt > now);

        var accountsWithIdentityKey = await ctx.Users.CountAsync(u => u.AccountIdentityPublicKey != null);
        var accounts = await ctx.Users.CountAsync();

        return Results.Ok(new MlsCertificateCoverageDto
        {
            ActiveDevices = active,
            DevicesWithValidCertificate = certified,
            CoverageRatio = active == 0 ? 0 : (double)certified / active,
            AccountsWithIdentityKey = accountsWithIdentityKey,
            TotalAccounts = accounts,
            CurrentEnforcement = MlsPolicy.CertificateEnforcement,
        });
    }
}

public class MlsPolicyDto
{
    public CertificateEnforcement CertificateEnforcement { get; set; }
    public string MinClientVersion { get; set; } = "";
    public bool RequireDeviceIdOnWelcomeFetch { get; set; }
    public bool RejectUnreachableDevicesOnCreate { get; set; }
}

public class MlsCertificateCoverageDto
{
    public int ActiveDevices { get; set; }
    public int DevicesWithValidCertificate { get; set; }
    public double CoverageRatio { get; set; }
    public int AccountsWithIdentityKey { get; set; }
    public int TotalAccounts { get; set; }
    public CertificateEnforcement CurrentEnforcement { get; set; }
}
