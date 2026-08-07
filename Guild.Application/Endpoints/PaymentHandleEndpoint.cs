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
    /// <summary>
    /// Every member's sealed handles, with the content key for the calling device where that member
    /// has shared with it - and, separately, the plaintext phone numbers of the members who opted
    /// in to showing theirs here.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/payment-handles")]
    public async Task<IResult> GetAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] DeviceIdResolver devices,
        [NotBody] LedgerService ledger, [NotBody] PaymentHandleService handles,
        [NotBody] IMessageBus bus, [NotBody] HttpContext http, [NotBody] ClaimsPrincipal user)
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

        // The consent gate, and the only one there is.
        var sharingMembers = await handles.GetPhoneSharingMemberIdsAsync(guildId, members);

        // Short-circuited rather than left to the handler's own empty-list guard, so a household
        // where nobody shares a number never puts a message on the bus at all.
        List<SharedPhoneNumberDto> phoneNumbers = sharingMembers.Count == 0
            ? []
            : await ReadSharedPhoneNumbersAsync(bus, sharingMembers);

        return Results.Ok(new PaymentHandleDirectoryDto
        {
            GuildId = guildId,
            DeviceId = deviceId,
            MemberRosterVersion = LedgerService.ComputeRosterVersion(members),
            Members = await handles.ReadForDeviceAsync(guildId, deviceId, members),
            PhoneNumbers = phoneNumbers,
            SharingPhoneNumber = sharingMembers.Contains(userId, StringComparer.Ordinal),
        });
    }

    /// <summary>Fetches the numbers of an already-consented set of members from Identity.</summary>
    private static async Task<List<SharedPhoneNumberDto>> ReadSharedPhoneNumbersAsync(
        IMessageBus bus, List<string> sharingMemberIds)
    {
        var consented = sharingMemberIds.ToHashSet(StringComparer.Ordinal);

        var response = await bus.InvokeAsync<GetUserPhoneNumbersResponse>(
            new GetUserPhoneNumbersRequest { UserIds = sharingMemberIds });

        return response.PhoneNumbers
            .Where(p => consented.Contains(p.UserId))
            .OrderBy(p => p.UserId, StringComparer.Ordinal)
            .Select(p => new SharedPhoneNumberDto
            {
                UserId = p.UserId,
                PhoneNumber = p.PhoneNumber,
                UpdatedAt = p.UpdatedAt,
            })
            .ToList();
    }

    /// <summary>Turns the caller's own phone number on or off for this guild.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/payment-handles/phone-sharing")]
    public async Task<IResult> SetPhoneSharingAsync(string guildId, SetPhoneSharingDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] PaymentHandleService handles,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Ledger))
            return Results.Forbid();

        // Membership is checked by the write finding a member row, not by a separate roster read:
        // the two cannot then disagree, and a non-member has nothing to write to.
        if (!await handles.SetPhoneSharingAsync(guildId, userId, dto.Share)) return Results.Forbid();

        await ctx.SaveChangesAsync();

        return Results.Ok(new { GuildId = guildId, SharingPhoneNumber = dto.Share });
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
                    // The certificate travels with the key it is issued over, in one response, so
                    // the server cannot pair one device's key with another device's certificate.
                    Certificate = d.Certificate,
                    CertificateIssuedAt = d.CertificateIssuedAt,
                    CertificateExpiresAt = d.CertificateExpiresAt,
                    IdentityKeyVersion = d.CertificateIdentityKeyVersion,
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
