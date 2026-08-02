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
    /// <summary>Ceiling on a single upload. The replenish flow never asks for more than the target
    /// count in one go, so anything past this is a client bug or an attempt to fill the table.</summary>
    public const int MaxKeyPackagesPerUpload = 200;

    /// <summary>
    /// Gate for the three operations that destroy a device's ability to participate: purging its key
    /// packages, rotating its identity key (which purges them), and removing it outright (which
    /// cascades away its encrypted backup).
    ///
    /// <para>All three took nothing but a session token and a client device id. Any of them, aimed at
    /// another of the account's devices, leaves that device unreachable or wipes the only copy of its
    /// signing key - so they are allowed from the device itself (the ordinary sign-out and
    /// state-wipe-recovery cases, where the session <i>is</i> that device) and otherwise cost the
    /// account password.</para>
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

    /// <summary>
    /// Registers a device, or re-registers one that already exists.
    ///
    /// <para><b>Re-registration is not a no-op.</b> It used to hand the existing row straight back,
    /// which meant a client that had just minted a fresh Ed25519 identity keypair - the normal
    /// consequence of a keychain miss or a local state wipe - kept the server advertising the *old*
    /// public key and the *old* key packages. Every Welcome anyone sealed to those packages was then
    /// undecryptable by the only device it was addressed to, silently and permanently: the private
    /// init keys had gone with the wipe. So a differing identity key is treated as a rotation - the
    /// stale stock is purged and the caller is told to re-upload.</para>
    /// </summary>
    [Authorize]
    [WolverinePost("api/v1/devices")]
    public async Task<(IResult, DeviceRegistered?)> CreateDevice(CreateMLSDeviceDto dto,
        [NotBody] IMessageBus messageBus, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx,
        [NotBody] SessionDeviceResolver sessionDevices, [NotBody] IAccountPasswordVerifier passwords)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var now = DateTimeOffset.UtcNow;

        // Structure and expiry only - the server does not hold the account identity private half and
        // therefore cannot check the signature. See DeviceCertificate for why refusing the malformed
        // ones here is still worth doing.
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

            if (rotated)
            {
                // Rotating another device's identity key empties its key-package stock, which makes
                // it unreachable for every new group until it next launches and replenishes. That is
                // a denial of service on somebody else's handset for the price of a session token,
                // so it is gated exactly like the other two destructive device operations.
                var denied = await RequireSelfOrPasswordAsync(user, userId, existingDevice, dto.Password,
                    "Rotating a device identity key", sessionDevices, passwords, ctx);
                if (denied is not null) return (denied, null);

                existingDevice.IdentityPublicKey = incomingKey;
                existingDevice.UpdatedAt = now;

                // Every package on file was generated by the identity that just went away. Leaving
                // them would keep the server handing out packages whose private init keys no longer
                // exist - the exact failure this rotation is a symptom of.
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
        // session cannot have been bound at /connect/token. Claim it here, and only here: binding a
        // session to a row that already existed is precisely how a stolen session would appoint
        // itself the laptop and read the laptop's backup.
        await sessionDevices.TryBindAsync(user, userId, device.Id);

        return (Results.Ok(ToRegistrationDto(device, identityRotated: false)), new DeviceRegistered()
        {
            DeviceId = device.Id,
            ClientDeviceId = device.ClientDeviceId,
            DeviceName = device.DeviceName,
            UserId = device.UserId,
        });
    }

    /// <summary>
    /// Throws away every key package this device has on file.
    ///
    /// <para>The counterpart to a client-side MLS state wipe, and the one route that makes such a
    /// wipe recoverable. The replenish count is derived purely from server rows, so a device that
    /// destroyed its local store while the server still held ~100 unconsumed packages was told to
    /// generate zero - and every Welcome sealed to those packages was undecryptable by the device it
    /// was addressed to, with nothing to signal it. Clients must call this <i>before</i>
    /// re-uploading, not after.</para>
    ///
    /// <para>Consumed and last-resort rows go too: they are just as dead, and keeping the last-resort
    /// package would leave the device reachable through a key it can no longer open.</para>
    /// </summary>
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
            // distinguishable case. Answering 403 rather than 404 tells an honest client that it is
            // signed in as the wrong account, which is the mistake that actually happens; the id is
            // a client-generated opaque value, so confirming one exists reveals nothing usable.
            var claimedByAnotherUser = await ctx.UserDevices.AnyAsync(d => d.ClientDeviceId == deviceId);
            return claimedByAnotherUser ? Results.Forbid() : Results.NotFound();
        }

        // Emptying another device's stock strands it: it is handed out no packages, so it cannot be
        // added to any new group until it next launches. Self-service from the device itself, the
        // password from anywhere else.
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

    /// <summary>
    /// Reissues a device's certificate without re-registering it.
    ///
    /// <para>Any device holding the account identity key may issue for any of the account's devices,
    /// which is what makes a 180-day expiry cheap rather than a recurring lockout. A device that
    /// nobody is willing to reissue for stops being verifiable, which is the point of the
    /// expiry.</para>
    /// </summary>
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

    private static void ApplyCertificate(UserDevice device, CreateMLSDeviceDto dto)
    {
        if (dto.DeviceCertificate is null or { Length: 0 }) return;

        device.Certificate = dto.DeviceCertificate;
        device.CertificateIssuedAt = dto.CertificateIssuedAt;
        device.CertificateExpiresAt = dto.CertificateExpiresAt;
        device.CertificateIdentityKeyVersion = dto.CertificateIdentityKeyVersion;
    }

    /// <summary>
    /// Records what this build of the client can actually do.
    ///
    /// <para>Capabilities rather than a version string, because the questions the server needs to
    /// answer are "can every device on this account verify a signed protection level" and "what
    /// fraction of live devices carry certificates" - and a version number only answers those by
    /// way of a lookup table somebody has to keep current. An unrecognised capability is stored and
    /// ignored, so a newer client never has to wait for the server to learn its vocabulary.</para>
    ///
    /// <para>Absent means "told us nothing", not "supports nothing new" - but it is treated as the
    /// latter everywhere it gates something, because a device that cannot say it understands the
    /// strict tier must not be assumed to.</para>
    ///
    /// <para><b>Additive on re-registration.</b> The list used to be replaced wholesale, so
    /// <c>capabilities: []</c> from a session token silently erased them - and since a device that
    /// cannot claim <c>ProtectionLevelV1</c> blocks the whole account from entering
    /// <c>VerifiedDevices</c>, that was an unauthenticated way to hold an account out of the strict
    /// tier indefinitely, while the downgrade it mirrors costs the password. A capability is a
    /// statement about a build; builds do not lose features on a relaunch. A device that genuinely
    /// needs to shed one is removed and re-registered.</para>
    /// </summary>
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

    // GET api/v1/devices lives on MlsDeviceController. A second registration of the same route used
    // to sit here, which additionally returned a non-awaited Task inside Results.Ok - serializing
    // the task rather than the devices.

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
        // material and the joining leaf has no forward secrecy from that point back. That is a
        // deliberate availability trade the permissive tier makes and the strict tier does not - it
        // is most of what VerifiedDevices is paying its usability cost for. Refused rather than
        // silently dropped: a client that thinks it uploaded a floor and did not is a client that
        // believes it is reachable when it is not.
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

        // An empty upload is a success, not a client error. The replenish flow asks how many
        // packages to generate and posts the result unconditionally, so a device that is already
        // fully stocked posts an empty list on every launch - rejecting that would fail a perfectly
        // correct client doing exactly what it was told.
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
            // GetCipherSuite rejects anything that is not a well-formed KeyPackage header. Accepting
            // a malformed package here just moves the failure into someone else's add_members call,
            // where it is far harder to attribute.
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
    /// packages, its encrypted backup and its push tokens - so a handset that has been wiped,
    /// sold or signed out stops being handed key packages for new groups and stops being rung.
    /// Login sessions from that device are revoked rather than deleted; they are the audit trail
    /// the user reads on the sessions screen.
    ///
    /// <para>Until this existed there was no removal path at all: <c>DeviceRemoved</c> was declared
    /// and never published, and a device's push tokens outlived it indefinitely.</para>
    ///
    /// <para><b>The cascade is the reason this is gated.</b> Deleting the row takes the device's
    /// encrypted backup with it - the only cloud copy of its signing key - and did so with no device
    /// check and no re-authentication, which walked straight around <c>DELETE .../backup</c>'s own
    /// guard. Signing yourself out is free; removing a <i>different</i> device costs the password,
    /// which is also the case where the user is deliberately acting on a handset they no longer
    /// hold.</para>
    ///
    /// <para>The certificate is revoked rather than merely forgotten. It is a public 180-day
    /// statement peers have already downloaded, so dropping the server's copy changes nothing for
    /// them; see <see cref="RevokedDeviceCertificate"/>.</para>
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
    /// Binds the caller's session to one of the account's already-registered devices, at the cost of
    /// the account password.
    ///
    /// <para><b>Why this route has to exist.</b> Backup reads, backup writes and device-to-device
    /// transfers are all gated on which device the session was established from (C2/§L.7), and a
    /// session only learns that at <c>/connect/token</c>. Every session issued before that plumbing
    /// landed has no device, and so does every session from a client that does not send the id yet -
    /// so on the day the gate goes live those callers lose all of those routes at once, with no way
    /// back except a full sign-out. There is nothing to backfill from (see
    /// <see cref="SessionDeviceResolver"/>), and a time-based grace window would simply switch C2 back
    /// on for a while. This is the remaining answer: pay the credential, keep the session.</para>
    ///
    /// <para><b>The password is not friction, it is the whole mechanism.</b> Binding to an existing
    /// device row is exactly the move a stolen session would make to appoint itself the laptop and
    /// read the laptop's backup, which is why <see cref="SessionDeviceResolver.TryBindAsync"/> refuses
    /// it outright on the registration path. It is only safe here because the caller proves the
    /// account, and it grants nothing that the password grant at <c>/connect/token</c> would not have
    /// granted anyway - that route binds a fresh session to any named device on the same evidence.</para>
    ///
    /// <para>Refuses to <i>re</i>-bind. A session that is already some device does not become another
    /// one; letting it would turn one password into a tour of every backup on the account.</para>
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
    ///
    /// <para>Readable by any authenticated caller, exactly like the account identity key it chains
    /// to: it is a list of hashes of public objects, and it is useless unless the peers who need to
    /// consult it can. Withholding it would only protect the revoked certificate.</para>
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

    // GET api/v1/devices/{deviceId}/key-packages is gone. It handed back the device's unconsumed
    // key packages in full without consuming them, so anything holding the user's token could
    // enumerate the supply. Clients only ever needed the *count*, which
    // MlsDeviceController.GetGenerateTokenCommandAsync already returns.
}
