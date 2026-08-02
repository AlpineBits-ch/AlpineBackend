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
///
/// <para><b>Why this is a server-side policy and not a client constant.</b> §H.4 says a leaf whose
/// device certificate is missing or invalid gets proposed for removal. No device in the field has a
/// certificate. A client that shipped that rule on its own judgement would begin removing every
/// other leaf in every group it is in, starting with its owner's other devices, and there would be
/// no way to stop it short of a client release. So the phase is served from here, cached briefly,
/// and <b>defaults to Observe whenever it cannot be read or parsed</b> - the failure mode of an
/// unreachable policy endpoint must be "do nothing", never "start removing".</para>
/// </summary>
[Authorize]
public class MlsPolicyEndpoint
{
    /// <summary>
    /// Where enforcement currently sits, plus the floor a client must meet before the tightened
    /// contracts (required <c>deviceId</c> on the Welcome fetch, rejecting creation on unreachable
    /// devices) apply to it.
    ///
    /// <para>Configuration, not computation: flipping this is a deliberate operational act taken on
    /// the coverage number below, not something the server decides for itself.</para>
    /// </summary>
    // Not "api/v1/identity/mls-policy". The gateway matches /api/v1/identity/{**rest} and forwards
    // /api/v1/{**rest}, so an internal route that repeats the prefix is only reachable at the absurd
    // /api/v1/identity/identity/mls-policy - and this one was, which made the enforcement kill switch
    // inert in production while failing silently, because an unreadable policy correctly defaults to
    // Observe. See GatewayRouteContractTests.
    [WolverineGet("api/v1/mls-policy")]
    public static IResult Get() => Results.Ok(new MlsPolicyDto
    {
        CertificateEnforcement = MlsPolicy.CertificateEnforcement,
        MinClientVersion = MlsPolicy.MinClientVersion,
        RequireDeviceIdOnWelcomeFetch = MlsPolicy.RequireDeviceIdOnWelcomeFetch,
        RejectUnreachableDevicesOnCreate = MlsPolicy.RejectUnreachableDevicesOnCreate,
    });

    /// <summary>
    /// What fraction of active devices carry an unexpired certificate, and how many accounts have an
    /// identity key at all.
    ///
    /// <para>Exists so advancing to <see cref="CertificateEnforcement.Enforce"/> is a decision made
    /// on data rather than on a guess about how the rollout is going. Enforcing below roughly 99%
    /// coverage means proposing the removal of real devices belonging to real users whose only
    /// mistake was not having opened the app yet.</para>
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
