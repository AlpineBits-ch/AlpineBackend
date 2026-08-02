using System.Security.Claims;
using Facet.Extensions;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Domain;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

public class MlsDeviceEndpoint
{
    /// <summary>Ceiling on a single upload.</summary>
    public const int MaxKeyPackagesPerUpload = 200;

    /// <summary>
    /// Gate for the three operations that destroy a device's ability to participate: purging its
    /// key packages, rotating its identity key (which purges them), and removing it outright (which
    /// cascades away its encrypted backup).
    /// </summary>
    private static async Task<IResult?> RequireSelfOrPasswordAsync(
        ClaimsPrincipal principal,
        string userId,
        UserDevice device,
        string? password,
        string action,
        SessionDeviceResolver sessionDevices,
        IAccountPasswordVerifier passwords,
        MicroserviceContext ctx)
    {
        if (await sessionDevices.IsCallingDeviceAsync(principal, userId, device.Id)) return null;

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Results.Unauthorized();

        var check = await passwords.CheckAsync(user, password);
        if (check.IsOk()) return null;

        return Results.BadRequest(
            $"{check.Describe(action)} ({action} a device other than the one you are signed in on "
            + "requires the account password.)");
    }

    /// <summary>Registers a device, or re-registers one that already exists.</summary>
    [Authorize]
    [WolverinePost("api/v1/devices")]
    public async Task<(IResult, DeviceRegistered?)> CreateDevice(CreateMLSDeviceDto dto,
        [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] SessionDeviceResolver sessionDevices, [NotBody] IAccountPasswordVerifier passwords)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var now = DateTimeOffset.UtcNow;

        // Structure and expiry only - the server does not hold the account identity private half
        // and therefore cannot check the signature.
        var certificateError = DeviceCertificate.Validate(
            dto.DeviceCertificate, dto.CertificateIssuedAt, dto.CertificateExpiresAt, now);
        if (certificateError is not null) return (Results.BadRequest(certificateError), null);

        var existingDevice = await ctx.UserDevices.FirstOrDefaultAsync(x => x.ClientDeviceId == dto.ClientDeviceId && x.UserId == userId);

        // A collision with another user's device used to delete that user's row (and cascade away
        // their key packages and backup) purely because ClientDeviceId is client-supplied and was
        // globally unique - any account could destroy another's device registration by claiming its
        // id. The id is scoped per user now, so a collision across users simply isn't one.

        if (existingDevice is not null)
        {
            var incomingKey = dto.IdentityPublicKey ?? [];
            var rotated = incomingKey.Length > 0
                          && !incomingKey.AsSpan().SequenceEqual(existingDevice.IdentityPublicKey);

            // Decided before the rotation gate, because for the whole mobile installed base the
            // gate is otherwise unpassable.
            var claim = await sessionDevices.TryClaimAsync(user, userId, existingDevice);

            if (claim == SessionDeviceResolver.ClaimResult.Claimed)
            {
                // The concession is bounded but real, so it leaves a trace: if a session is ever
                // found reading the wrong device's backup, this row is where it started.
                ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
                {
                    UserId = userId,
                    Action = IdentityAuditActions.SessionDeviceBound,
                    ClientDeviceId = existingDevice.ClientDeviceId,
                    Detail = "session adopted an unclaimed device row during registration",
                    CreatedAt = now,
                }));
            }

            if (rotated)
            {
                // Rotating another device's identity key empties its key-package stock, which makes
                // it unreachable for every new group until it next launches and replenishes.
                if (!SessionDeviceResolver.IsSelf(claim))
                {
                    var denied = await RequireSelfOrPasswordAsync(user, userId, existingDevice, dto.Password,
                        "Rotating a device identity key", sessionDevices, passwords, ctx);
                    if (denied is not null) return (denied, null);
                }

                existingDevice.IdentityPublicKey = incomingKey;
                existingDevice.UpdatedAt = now;

                // Every package on file was generated by the identity that just went away.
                var purged = await PurgeKeyPackagesAsync(ctx, existingDevice.Id);

                // The two destructive device operations used to leave no trace at all, despite the
                // action constant existing for exactly this.
                ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
                {
                    UserId = userId,
                    Action = IdentityAuditActions.DeviceIdentityRotated,
                    ClientDeviceId = existingDevice.ClientDeviceId,
                    Detail = $"identity key replaced; {purged} key package(s) purged",
                    CreatedAt = now,
                }));
            }

            // The name and type are cheap to keep current; a renamed handset should not need a
            // second endpoint to say so.
            if (!string.IsNullOrWhiteSpace(dto.DeviceName)) existingDevice.DeviceName = dto.DeviceName;

            ApplyCertificate(existingDevice, dto);
            ApplyCapabilities(existingDevice, dto);

            return (
                Results.Ok(ToRegistrationDto(existingDevice, rotated)),
                rotated
                    ? new DeviceRegistered
                    {
                        DeviceId = existingDevice.Id,
                        ClientDeviceId = existingDevice.ClientDeviceId,
                        DeviceName = existingDevice.DeviceName,
                        UserId = existingDevice.UserId,
                        IdentityRotated = true,
                    }
                    : null);
        }

        var device = UserDevice.Create(new CreateUserDeviceParams()
        {
            UserId = userId,
            ClientDeviceId = dto.ClientDeviceId,
            DeviceName = dto.DeviceName,
            DeviceType = dto.DeviceType,
            IdentityPublicKey = dto.IdentityPublicKey,
        });
        ApplyCertificate(device, dto);
        ApplyCapabilities(device, dto);
        ctx.UserDevices.Add(device);

        // A device's very first launch necessarily logs in before it can register itself, so its
        // session cannot have been bound at /connect/token.
        await sessionDevices.TryClaimAsync(user, userId, device);

        return (Results.Ok(ToRegistrationDto(device, identityRotated: false)), new DeviceRegistered()
        {
            DeviceId = device.Id,
            ClientDeviceId = device.ClientDeviceId,
            DeviceName = device.DeviceName,
            UserId = device.UserId,
        });
    }

    /// <summary>Throws away every key package this device has on file.</summary>
    [Authorize]
    [WolverineDelete("api/v1/devices/client/{deviceId}/key-packages")]
    public static async Task<IResult> ResetKeyPackages(string deviceId,
        [FromQuery] string? password,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] SessionDeviceResolver sessionDevices, [NotBody] IAccountPasswordVerifier passwords)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var device = await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ClientDeviceId == deviceId && d.UserId == userId);

        if (device is null)
        {
            // ClientDeviceId is only unique per user, so "exists but is not yours" is a real and
            // distinguishable case.
            var claimedByAnotherUser = await ctx.UserDevices.AnyAsync(d => d.ClientDeviceId == deviceId);
            return claimedByAnotherUser ? Results.Forbid() : Results.NotFound();
        }

        // Emptying another device's stock strands it: it is handed out no packages, so it cannot be
        // added to any new group until it next launches.
        var denied = await RequireSelfOrPasswordAsync(user, userId, device, password,
            "Resetting a device's key packages", sessionDevices, passwords, ctx);
        if (denied is not null) return denied;

        var deleted = await PurgeKeyPackagesAsync(ctx, device.Id);

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.KeyPackagesReset,
            ClientDeviceId = device.ClientDeviceId,
            Detail = $"{deleted} key package(s) purged",
            CreatedAt = DateTimeOffset.UtcNow,
        }));

        await ctx.SaveChangesAsync();

        return Results.Ok(new ResetKeyPackagesResultDto { DeletedCount = deleted });
    }

    /// <summary>Reissues a device's certificate without re-registering it.</summary>
    [Authorize]
    [WolverinePut("api/v1/devices/client/{deviceId}/certificate")]
    public static async Task<IResult> UpdateCertificate(string deviceId, UpdateDeviceCertificateDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var now = DateTimeOffset.UtcNow;
        if (dto.Certificate is null or { Length: 0 }) return Results.BadRequest("certificate is required");

        var error = DeviceCertificate.Validate(dto.Certificate, dto.IssuedAt, dto.ExpiresAt, now);
        if (error is not null) return Results.BadRequest(error);

        var device = await ctx.UserDevices
            .FirstOrDefaultAsync(d => d.ClientDeviceId == deviceId && d.UserId == userId);
        if (device is null) return Results.NotFound();

        device.Certificate = dto.Certificate;
        device.CertificateIssuedAt = dto.IssuedAt;
        device.CertificateExpiresAt = dto.ExpiresAt;
        device.CertificateIdentityKeyVersion = dto.IdentityKeyVersion;
        device.UpdatedAt = now;

        return Results.Ok(new { deviceId, expiresAt = dto.ExpiresAt });
    }

    /// <summary>One device's certificate, for a peer verifying the leaf it vouches for.</summary>
    [Authorize]
    [WolverineGet("api/v1/users/{userId}/devices/{deviceId}/certificate")]
    public static async Task<IResult> GetDeviceCertificate(string userId, string deviceId,
        [NotBody] MicroserviceContext ctx)
    {
        var device = await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ClientDeviceId == deviceId);

        // 404 for "no such device" and for "no certificate" alike.
        if (device?.Certificate is not { Length: > 0 }) return Results.NotFound();
        if (device.CertificateIssuedAt is null || device.CertificateExpiresAt is null)
            return Results.NotFound();

        return Results.Ok(new DeviceCertificateDto
        {
            DeviceId = device.ClientDeviceId,
            DeviceSignatureKey = device.IdentityPublicKey,
            Certificate = device.Certificate,
            IssuedAt = device.CertificateIssuedAt.Value.ToUnixTimeSeconds(),
            ExpiresAt = device.CertificateExpiresAt.Value.ToUnixTimeSeconds(),
            IdentityKeyVersion = device.CertificateIdentityKeyVersion,
        });
    }

    private static void ApplyCertificate(UserDevice device, CreateMLSDeviceDto dto)
    {
        if (dto.DeviceCertificate is null or { Length: 0 }) return;

        device.Certificate = dto.DeviceCertificate;
        device.CertificateIssuedAt = dto.CertificateIssuedAt;
        device.CertificateExpiresAt = dto.CertificateExpiresAt;
        device.CertificateIdentityKeyVersion = dto.CertificateIdentityKeyVersion;
    }

    /// <summary>Records what this build of the client can actually do.</summary>
    private static void ApplyCapabilities(UserDevice device, CreateMLSDeviceDto dto)
    {
        if (dto.Capabilities is null) return;

        device.Capabilities = device.Capabilities
            .Concat(dto.Capabilities)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Removes every key package row for a device row id and reports how many went.
    /// Deliberately unfiltered - a consumed or expired package is no more usable than a live one
    /// whose private half has been destroyed.</summary>
    private static async Task<int> PurgeKeyPackagesAsync(MicroserviceContext ctx, string deviceRowId)
    {
        var packages = await ctx.UserKeyPackages.Where(p => p.DeviceId == deviceRowId).ToListAsync();
        if (packages.Count == 0) return 0;

        ctx.UserKeyPackages.RemoveRange(packages);
        return packages.Count;
    }

    /// <summary>The device as the client already reads it, plus the one bit it cannot infer: whether
    /// this call rotated the identity key and therefore emptied the key-package stock.</summary>
    private static DeviceRegistrationDto ToRegistrationDto(UserDevice device, bool identityRotated) =>
        new()
        {
            Id = device.Id,
            ClientDeviceId = device.ClientDeviceId,
            DeviceName = device.DeviceName,
            DeviceType = device.DeviceType,
            IdentityPublicKey = device.IdentityPublicKey,
            Status = device.Status,
            LastSeen = device.LastSeen,
            UserId = device.UserId,
            CreatedAt = device.CreatedAt,
            UpdatedAt = device.UpdatedAt,
            IdentityRotated = identityRotated,
        };

    // GET api/v1/devices lives on MlsDeviceController.

    [Authorize]
    [WolverinePost("api/v1/devices/client/{deviceId}/key-packages")]
    public async Task<IResult> AddKeyPackagesForDeviceAsync(string deviceId, [FromBody] AddMLSDeviceKeyPackagesDto dto, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var device = await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientDeviceId == deviceId && x.UserId == userId);
        if(device is null) return Results.NotFound();

        var incoming = dto.KeyPackages ?? [];

        // A last-resort package is reusable, so every group that joins through one shares init key
        // material and the joining leaf has no forward secrecy from that point back.
        if (incoming.Any(p => p.IsLastResort))
        {
            var protectionLevel = await ctx.Users
                .Where(u => u.Id == userId)
                .Select(u => u.ProtectionLevel)
                .FirstOrDefaultAsync();

            if (protectionLevel == ProtectionLevel.VerifiedDevices)
                return Results.BadRequest(
                    "This account is on VerifiedDevices, which does not permit the reusable " +
                    "last-resort key package. Upload single-use packages only.");
        }

        // An empty upload is a success, not a client error.
        if (incoming.Count == 0) return Results.Ok(new AddKeyPackagesResultDto { Added = 0 });

        if (incoming.Count > MaxKeyPackagesPerUpload)
            return Results.BadRequest($"At most {MaxKeyPackagesPerUpload} key packages per upload");

        List<UserKeyPackage> packages;
        try
        {
            packages = incoming.Select(p => UserKeyPackage.Create(new CreateUserKeyPackageParams()
            {
                UserId = userId,
                DeviceId = device.Id,
                KeyPackage = p.KeyPackage,
                ExpiresAt = p.ExpiresAt,
                IsLastResort = p.IsLastResort,
            })).ToList();
        }
        catch (ArgumentException ex)
        {
            // GetCipherSuite rejects anything that is not a well-formed KeyPackage header.
            return Results.BadRequest(ex.Message);
        }

        // A device keeps exactly one last-resort package; a new one supersedes the old rather than
        // accumulating, which keeps the reuse window bounded to the newest key.
        if (packages.Any(p => p.IsLastResort))
        {
            var superseded = await ctx.UserKeyPackages
                .Where(p => p.DeviceId == device.Id && p.IsLastResort)
                .ToListAsync();
            ctx.UserKeyPackages.RemoveRange(superseded);
        }

        ctx.UserKeyPackages.AddRange(packages);

        return Results.Ok(new AddKeyPackagesResultDto { Added = packages.Count });
    }

    /// <summary>
    /// Unregisters one of the caller's own devices: the row goes, and with it (by cascade) its key
    /// packages, its encrypted backup and its push tokens - so a handset that has been wiped, sold
    /// or signed out stops being handed key packages for new groups and stops being rung.
    /// </summary>
    [Authorize]
    [WolverineDelete("api/v1/devices/client/{deviceId}")]
    public static async Task<(IResult, DeviceRemoved?)> RemoveDevice(string deviceId,
        [FromQuery] string? password,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] SessionDeviceResolver sessionDevices, [NotBody] IAccountPasswordVerifier passwords)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var device = await ctx.UserDevices
            .FirstOrDefaultAsync(d => d.ClientDeviceId == deviceId && d.UserId == userId);
        if (device is null) return (Results.NotFound(), null);

        var denied = await RequireSelfOrPasswordAsync(user, userId, device, password,
            "Removing", sessionDevices, passwords, ctx);
        if (denied is not null) return (denied, null);

        var now = DateTimeOffset.UtcNow;

        var sessions = await ctx.LoginSessions
            .Where(s => s.DeviceId == device.Id && s.RevokedAt == null)
            .ToListAsync();
        foreach (var session in sessions)
        {
            session.Revoke();
        }

        if (device.Certificate is { Length: > 0 } certificate)
        {
            var fingerprint = RevokedDeviceCertificate.Fingerprint(certificate);
            var alreadyRevoked = await ctx.RevokedDeviceCertificates
                .AnyAsync(r => r.UserId == userId && r.CertificateFingerprint == fingerprint);

            if (!alreadyRevoked)
            {
                ctx.RevokedDeviceCertificates.Add(RevokedDeviceCertificate.Create(
                    new CreateRevokedDeviceCertificateParams
                    {
                        UserId = userId,
                        ClientDeviceId = device.ClientDeviceId,
                        Certificate = certificate,
                        IdentityKeyVersion = device.CertificateIdentityKeyVersion,
                        CertificateExpiresAt = device.CertificateExpiresAt,
                        Reason = CertificateRevocationReasons.DeviceRemoved,
                        RevokedAt = now,
                    }));
            }
        }

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.DeviceRemoved,
            ClientDeviceId = device.ClientDeviceId,
            Detail = $"{device.DeviceName} removed; {sessions.Count} session(s) revoked"
                     + (device.Certificate is { Length: > 0 } ? "; certificate revoked" : ""),
            CreatedAt = now,
        }));

        ctx.UserDevices.Remove(device);

        return (Results.NoContent(), new DeviceRemoved
        {
            UserId = userId,
            DeviceId = device.Id,
            ClientDeviceId = device.ClientDeviceId,
        });
    }

    /// <summary>
    /// Binds the caller's session to one of the account's already-registered devices, at the cost
    /// of the account password.
    /// </summary>
    [Authorize]
    [WolverinePost("api/v1/devices/client/{deviceId}/bind-session")]
    public static async Task<IResult> BindSessionToDevice(string deviceId, BindSessionDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] SessionDeviceResolver sessionDevices, [NotBody] IAccountPasswordVerifier passwords)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var device = await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ClientDeviceId == deviceId
                                      && d.UserId == userId
                                      && d.Status == DeviceStatus.Active);

        if (device is null)
        {
            var claimedByAnotherUser = await ctx.UserDevices.AnyAsync(d => d.ClientDeviceId == deviceId);
            return claimedByAnotherUser ? Results.Forbid() : Results.NotFound();
        }

        var account = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (account is null) return Results.Unauthorized();

        var check = await passwords.CheckAsync(account, dto.Password);
        if (!check.IsOk())
        {
            return Results.Json(
                new
                {
                    error = "credential_required",
                    detail = check.Describe("Binding this session to a device")
                             + " (Naming which device a session is decides which device's backup it "
                             + "may read, so it costs the account password rather than the session "
                             + "token that is already being questioned.)",
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var result = await sessionDevices.BindExistingAsync(user, userId, device.Id);

        switch (result)
        {
            case SessionDeviceResolver.BindResult.NoSession:
                return Results.Json(
                    new
                    {
                        error = "session_untracked",
                        detail = "This token carries no usable session, so there is nothing to bind. "
                                 + "Sign in again with the X-Device-Id header set.",
                    },
                    statusCode: StatusCodes.Status403Forbidden);

            case SessionDeviceResolver.BindResult.BoundElsewhere:
                return Results.Conflict(new
                {
                    error = "session_bound_elsewhere",
                    detail = "This session already belongs to a different device. Sign in from the "
                             + "device you want to act as instead of re-pointing this session at it.",
                });

            case SessionDeviceResolver.BindResult.AlreadyBound:
                return Results.Ok(new BindSessionResultDto { DeviceId = device.ClientDeviceId, Bound = false });

            case SessionDeviceResolver.BindResult.Bound:
            default:
                ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
                {
                    UserId = userId,
                    Action = IdentityAuditActions.SessionDeviceBound,
                    ClientDeviceId = device.ClientDeviceId,
                    Detail = "session bound to an existing device with the account password",
                }));

                return Results.Ok(new BindSessionResultDto { DeviceId = device.ClientDeviceId, Bound = true });
        }
    }

    /// <summary>
    /// The account's revoked device certificates, for a peer to check a certificate against before
    /// accepting the leaf it vouches for.
    /// </summary>
    [Authorize]
    [WolverineGet("api/v1/users/{userId}/revoked-certificates")]
    public static async Task<IResult> GetRevokedCertificates(string userId, [NotBody] MicroserviceContext ctx)
    {
        var rows = await ctx.RevokedDeviceCertificates.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.RevokedAt)
            .Select(r => new RevokedCertificateDto
            {
                DeviceId = r.ClientDeviceId,
                CertificateFingerprint = r.CertificateFingerprint,
                IdentityKeyVersion = r.IdentityKeyVersion,
                CertificateExpiresAt = r.CertificateExpiresAt,
                Reason = r.Reason,
                RevokedAt = r.RevokedAt,
            })
            .ToListAsync();

        return Results.Ok(rows);
    }

    // GET api/v1/devices/{deviceId}/key-packages is gone.
}
