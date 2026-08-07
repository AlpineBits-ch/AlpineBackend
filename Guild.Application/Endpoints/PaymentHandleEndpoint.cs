using System.Security.Claims;
using Echo.Realtime.Devices;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// How each housemate wants to be paid back, sealed so that this service cannot read it.
/// </summary>
[Authorize]
public class PaymentHandleEndpoint
{
    /// <summary>Every member's sealed handles, with the content key for the calling device where
    /// that member has shared with it.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/payment-handles")]
    public async Task<IResult> GetAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] DeviceIdResolver devices,
        [NotBody] LedgerService ledger, [NotBody] PaymentHandleService handles,
        [NotBody] HttpContext http, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Ledger))
            return Results.Forbid();

        var members = await ledger.GetGuildMemberIdsAsync(guildId);
        if (!members.Contains(userId, StringComparer.Ordinal)) return Results.Forbid();

        // Fail-closed, and a 400 rather than an empty result.
        var deviceId = await devices.ResolveVerifiedAsync(http.Request, userId);
        if (deviceId is null)
            return Results.BadRequest(
                $"A registered {DeviceIdentity.HeaderName} is required to read sealed payment handles");

        return Results.Ok(new PaymentHandleDirectoryDto
        {
            GuildId = guildId,
            DeviceId = deviceId,
            MemberRosterVersion = LedgerService.ComputeRosterVersion(members),
            Members = await handles.ReadForDeviceAsync(guildId, deviceId, members),
        });
    }

    /// <summary>
    /// Who a client has to seal to, and the public key to seal to each of them with.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/payment-handles/recipients")]
    public async Task<IResult> RecipientsAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] LedgerService ledger,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Ledger))
            return Results.Forbid();

        var members = await ledger.GetGuildMemberIdsAsync(guildId);
        if (!members.Contains(userId, StringComparer.Ordinal)) return Results.Forbid();

        var devices = await bus.InvokeAsync<GetUserDeviceKeysResponse>(
            new GetUserDeviceKeysRequest { UserIds = members });

        // Only members were asked about, so this filter should never remove anything.
        var memberSet = members.ToHashSet(StringComparer.Ordinal);

        return Results.Ok(new PaymentHandleRecipientsDto
        {
            GuildId = guildId,

            // Derived from the same member list the recipients came from, so a client that seals
            // against this response cannot store a version describing a roster it never saw.
            MemberRosterVersion = LedgerService.ComputeRosterVersion(members),

            // Already ordered by Identity; ordered again here so the response does not depend on
            // that staying true across a change on the other side of the bus.
            Recipients = devices.Devices
                .Where(d => memberSet.Contains(d.UserId))
                .OrderBy(d => d.UserId, StringComparer.Ordinal)
                .ThenBy(d => d.DeviceId, StringComparer.Ordinal)
                .Select(d => new PaymentHandleRecipientDto
                {
                    UserId = d.UserId,
                    DeviceId = d.DeviceId,
                    DeviceName = d.DeviceName,
                    PublicKey = d.PublicKey,
                    HasValidCertificate = d.HasValidCertificate,
                    CertificateRevokedAt = d.CertificateRevokedAt,
                    IsActive = d.IsActive,
                })
                .ToList(),

            // Passed through rather than dropped: a client that seals against a truncated roster
            // produces a blob some housemate silently cannot open, and this is the only signal that
            // says so.
            UnresolvedMemberIds = devices.OmittedUserIds.ToList(),
        });
    }

    /// <summary>Stores the caller's own sealed handles, replacing whatever was there.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/payment-handles")]
    public async Task<IResult> SealAsync(string guildId, SealPaymentHandlesDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] HouseholdChannelService household,
        [NotBody] LedgerService ledger, [NotBody] PaymentHandleService handles,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Ledger))
            return Results.Forbid();

        var members = await ledger.GetGuildMemberIdsAsync(guildId);
        if (!members.Contains(userId, StringComparer.Ordinal)) return Results.Forbid();

        var memberSet = members.ToHashSet(StringComparer.Ordinal);
        if (PaymentHandleService.Validate(dto, memberSet) is { } error) return Results.BadRequest(error);

        var rosterVersion = LedgerService.ComputeRosterVersion(members);

        await handles.SealAsync(guildId, userId, dto, rosterVersion);
        await ctx.SaveChangesAsync();

        // Not audited.
        await household.BroadcastGuildAsync(guildId, "guild.PaymentHandlesChanged",
            new { GuildId = guildId, UserId = userId, MemberRosterVersion = rosterVersion });

        return Results.Ok(new { GuildId = guildId, UserId = userId, MemberRosterVersion = rosterVersion });
    }

    /// <summary>Drops the caller's own blob and every wrap of it.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/payment-handles")]
    public async Task<IResult> DeleteAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] HouseholdChannelService household,
        [NotBody] PaymentHandleService handles, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Ledger))
            return Results.Forbid();

        // Idempotent: deleting details you never recorded is not an error, and a 404 here would
        // tell a caller whether a row existed, which is the one bit this route should not leak.
        if (!await handles.DeleteAsync(guildId, userId)) return Results.NoContent();

        await ctx.SaveChangesAsync();

        await household.BroadcastGuildAsync(guildId, "guild.PaymentHandlesChanged",
            new { GuildId = guildId, UserId = userId, Removed = true });

        return Results.NoContent();
    }
}
