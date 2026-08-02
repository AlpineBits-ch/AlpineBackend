using System.Security.Claims;
using Domain;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>The account's device-admission protection level.</summary>
[Authorize]
public class ProtectionLevelEndpoint
{
    /// <summary>How far the client's signed timestamp may sit from server time.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

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
        [NotBody] IAccountPasswordVerifier passwords,
        [NotBody] SessionDeviceResolver sessionDevices,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return (Results.NotFound(), null);

        // An unsigned level is not a level.
        if (dto.SignedAssertion is null or { Length: 0 })
            return (Results.BadRequest("signedAssertion is required"), null);

        var now = DateTimeOffset.UtcNow;

        // The signed timestamp, checked for sanity and then stored untouched.
        if (dto.UpdatedAt == default)
        {
            return (Results.BadRequest(
                "updatedAt is required: it is part of the signed payload, and the server stores it "
                + "verbatim so clients can reconstruct exactly what was signed."), null);
        }

        var skew = dto.UpdatedAt - now;
        if (skew > MaxClockSkew || skew < -MaxClockSkew)
        {
            return (Results.BadRequest(
                $"updatedAt is more than {MaxClockSkew.TotalMinutes:0} minutes from server time "
                + $"({now:O}). Check the device clock and re-sign."), null);
        }

        if (user.ProtectionLevelUpdatedAt is { } previousStamp && dto.UpdatedAt < previousStamp)
        {
            return (Results.BadRequest(
                "updatedAt must not move backwards; the stored assertion is newer than this one."), null);
        }

        // Optimistic concurrency on the version, not last-write-wins.
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

        // Upgrades need no ceremony - tightening your own account should never be gated.
        if (isDowngrade)
        {
            var check = await passwords.CheckAsync(user, dto.Password);
            if (!check.IsOk()) return (Results.BadRequest(check.Describe("Downgrading the protection level")), null);
        }

        var previous = user.ProtectionLevel;
        var actingDeviceId = (await sessionDevices.ResolveAsync(principal, userId))?.ClientDeviceId ?? dto.DeviceId;

        user.ProtectionLevel = dto.Level;
        user.ProtectionLevelAssertion = dto.SignedAssertion;
        user.ProtectionLevelVersion = dto.Version;
        user.ProtectionLevelUpdatedAt = dto.UpdatedAt;

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.ProtectionLevelChanged,
            ClientDeviceId = actingDeviceId,
            Detail = $"{previous} -> {dto.Level} (v{dto.Version})",
            CreatedAt = now,
        }));

        await ctx.SaveChangesAsync();

        return (Results.Ok(new ProtectionLevelDto
        {
            Level = dto.Level,
            SignedAssertion = dto.SignedAssertion,
            Version = dto.Version,
            UpdatedAt = dto.UpdatedAt,
        }), new ProtectionLevelChanged
        {
            UserId = userId,
            PreviousLevel = previous,
            Level = dto.Level,
            Version = dto.Version,
            SignedAssertion = dto.SignedAssertion,
            ChangedByDeviceId = actingDeviceId,
            IsDowngrade = isDowngrade,
            ChangedAt = now,
        });
    }

    /// <summary>
    /// Why this account cannot enter <see cref="ProtectionLevel.VerifiedDevices"/> yet, or null
    /// when it can.
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
