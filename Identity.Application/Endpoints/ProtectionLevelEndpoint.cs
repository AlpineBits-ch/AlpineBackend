using System.Security.Claims;
using Domain;
using Identity.Application.Dtos.Request;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>
/// The account's device-admission protection level.
///
/// <para><b>The server stores this; it does not decide it.</b> A plain server-side boolean would let
/// a hostile server flip a <see cref="ProtectionLevel.VerifiedDevices"/> account to
/// <see cref="ProtectionLevel.TrustedSignIn"/> and then auto-admit a device it controls - defeating
/// the entire tier. So the authority is <c>signedAssertion</c>, signed by the user's identity key
/// and verified independently by every client, which enforces the last validly-signed level it has
/// seen and fails closed to the stricter reading when it cannot verify one. The enum column exists
/// only so the server can answer "may this join request be auto-admitted" without a round trip to a
/// client, and being wrong about it changes nothing a client will act on.</para>
///
/// <para>Downgrades cost the account password, are broadcast to every device, and leave an
/// append-only audit row. A client that sees a downgrade it did not participate in is supposed to
/// warn loudly, and it can only do that if the downgrade is visible - hence all three.</para>
/// </summary>
[Authorize]
public class ProtectionLevelEndpoint
{
    [WolverineGet("api/v1/identity/protection-level")]
    public static async Task<IResult> Get(
        [NotBody] ClaimsPrincipal principal, [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var user = await ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new ProtectionLevelDto
            {
                Level = u.ProtectionLevel,
                SignedAssertion = u.ProtectionLevelAssertion,
                Version = u.ProtectionLevelVersion,
                UpdatedAt = u.ProtectionLevelUpdatedAt,
            })
            .FirstOrDefaultAsync();

        return user is null ? Results.NotFound() : Results.Ok(user);
    }

    [WolverinePut("api/v1/identity/protection-level")]
    public static async Task<(IResult, ProtectionLevelChanged?)> Put(
        PutProtectionLevelDto dto,
        [NotBody] ClaimsPrincipal principal,
        [NotBody] UserManager<ApplicationUser> users,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return (Results.NotFound(), null);

        // An unsigned level is not a level. Accepting one would make the column the authority again
        // by the back door - the server would be asserting something no client can check.
        if (dto.SignedAssertion is null or { Length: 0 })
            return (Results.BadRequest("signedAssertion is required"), null);

        // Optimistic concurrency on the version, not last-write-wins. Two devices changing the level
        // at once must not end with the account on the losing device's setting and the winning
        // device's assertion.
        if (dto.Version != user.ProtectionLevelVersion + 1)
        {
            return (Results.Conflict(new ProtectionLevelDto
            {
                Level = user.ProtectionLevel,
                SignedAssertion = user.ProtectionLevelAssertion,
                Version = user.ProtectionLevelVersion,
                UpdatedAt = user.ProtectionLevelUpdatedAt,
            }), null);
        }

        var isDowngrade = user.ProtectionLevel == ProtectionLevel.VerifiedDevices
                          && dto.Level == ProtectionLevel.TrustedSignIn;

        if (dto.Level == ProtectionLevel.VerifiedDevices && user.ProtectionLevel != ProtectionLevel.VerifiedDevices)
        {
            var blocked = await DescribeUpgradeBlockersAsync(ctx, user);
            // Refused with the reason and the offending device names, not a bare 400. "You cannot
            // turn this on" with no explanation is how a security setting ends up never used; the
            // client is supposed to say *which* device is holding the account back.
            if (blocked is not null) return (Results.BadRequest(blocked), null);
        }

        // Upgrades need no ceremony - tightening your own account should never be gated. Loosening
        // it is the move an attacker who holds a session wants to make, so it costs the password.
        if (isDowngrade && (string.IsNullOrEmpty(dto.Password) || !await users.CheckPasswordAsync(user, dto.Password)))
            return (Results.BadRequest("Downgrading the protection level requires the account password."), null);

        var previous = user.ProtectionLevel;
        var now = DateTimeOffset.UtcNow;

        user.ProtectionLevel = dto.Level;
        user.ProtectionLevelAssertion = dto.SignedAssertion;
        user.ProtectionLevelVersion = dto.Version;
        user.ProtectionLevelUpdatedAt = now;

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.ProtectionLevelChanged,
            ClientDeviceId = dto.DeviceId,
            Detail = $"{previous} -> {dto.Level} (v{dto.Version})",
            CreatedAt = now,
        }));

        await ctx.SaveChangesAsync();

        return (Results.Ok(new ProtectionLevelDto
        {
            Level = dto.Level,
            SignedAssertion = dto.SignedAssertion,
            Version = dto.Version,
            UpdatedAt = now,
        }), new ProtectionLevelChanged
        {
            UserId = userId,
            PreviousLevel = previous,
            Level = dto.Level,
            Version = dto.Version,
            SignedAssertion = dto.SignedAssertion,
            ChangedByDeviceId = dto.DeviceId,
            IsDowngrade = isDowngrade,
            ChangedAt = now,
        });
    }

    /// <summary>
    /// Why this account cannot enter <see cref="ProtectionLevel.VerifiedDevices"/> yet, or null when
    /// it can.
    ///
    /// <para>Two gates, both about not promising something that cannot be delivered:</para>
    ///
    /// <para><b>Every active device must understand the tier.</b> An old client cannot verify the
    /// signed assertion, so it would keep behaving as <see cref="ProtectionLevel.TrustedSignIn"/> -
    /// and a strict setting that one of your devices silently ignores is worse than no setting,
    /// because the user believes they are protected.</para>
    ///
    /// <para><b>The account needs a recovery-code envelope and an identity key.</b> Under this tier a
    /// server-assisted password reset must not be able to restore end-to-end encrypted history, so
    /// the recovery credential cannot be the password. Turning the tier on without one would sell a
    /// guarantee the storage layer does not implement, and the user would only find out at the worst
    /// possible moment.</para>
    /// </summary>
    private static async Task<ProtectionLevelUpgradeBlockedDto?> DescribeUpgradeBlockersAsync(
        MicroserviceContext ctx, ApplicationUser user)
    {
        var reasons = new List<string>();

        var unsupported = await ctx.UserDevices.AsNoTracking()
            .Where(d => d.UserId == user.Id && d.Status == DeviceStatus.Active)
            .Select(d => new { d.ClientDeviceId, d.DeviceName, d.Capabilities })
            .ToListAsync();

        var lagging = unsupported
            .Where(d => !d.Capabilities.Contains(MlsCapabilities.ProtectionLevelV1))
            .Select(d => d.DeviceName)
            .ToList();

        if (lagging.Count > 0)
            reasons.Add("These devices are on a build that cannot enforce VerifiedDevices; update or "
                        + $"remove them first: {string.Join(", ", lagging)}.");

        if (user.AccountIdentityPublicKey is null or { Length: 0 })
            reasons.Add("This account has no identity key yet. Unlock an updated client once so it "
                        + "can publish one.");

        if (user.RecoveryCodeWrappedMasterKey is null)
            reasons.Add("Set up a recovery code first. VerifiedDevices means a password reset cannot "
                        + "restore encrypted history, so a credential other than the password has to "
                        + "open the master key.");

        return reasons.Count == 0
            ? null
            : new ProtectionLevelUpgradeBlockedDto { BlockingDeviceNames = lagging, Reasons = reasons };
    }
}
