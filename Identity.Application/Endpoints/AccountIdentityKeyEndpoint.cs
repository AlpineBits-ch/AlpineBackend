using System.Security.Claims;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>
/// The account's long-lived Ed25519 identity key - public half only.
///
/// <para><b>What it is for.</b> §G's admission proof is verified against the account master key,
/// which only the owner's own devices hold. That works right up until the journey a cloud backup
/// exists to serve: every device gone, nothing online to verify anything. The account identity key
/// is the way out - its private half rides in the backup envelope, so a restored device can
/// self-issue a certificate, external-commit into each group, and have peers verify it offline
/// against the key they pinned. No device of the account needs to be online, and nobody has to take
/// the server's word for anything.</para>
///
/// <para><b>The server holds the public half and nothing else.</b> The private half is wrapped under
/// the recovery key inside the backup blob, which the server never parses. That is what makes the
/// certificate meaningful: a server that could mint one would turn the external-commit recovery path
/// into a backdoor into every group.</para>
/// </summary>
[Authorize]
public class AccountIdentityKeyEndpoint
{
    /// <summary>
    /// A user's account identity key, for TOFU-pinning by peers.
    ///
    /// <para>Readable by any authenticated caller, exactly like a Signal safety number: it is public
    /// key material whose whole purpose is being compared out of band. <c>Version</c> travels with it
    /// so a peer can tell a rotation from a rollback, and
    /// <c>rotationSignature</c> so continuity from the previous key can be verified automatically
    /// where it exists. Where it does not, the peer must show a safety-number-changed warning and
    /// wait for a human - never auto-accept.</para>
    /// </summary>
    [WolverineGet("api/v1/users/{userId}/identity-key")]
    public static async Task<IResult> Get(string userId, [NotBody] MicroserviceContext ctx)
    {
        var key = await ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new AccountIdentityKeyDto
            {
                UserId = u.Id,
                PublicKey = u.AccountIdentityPublicKey,
                Version = u.AccountIdentityKeyVersion,
                RotationSignature = u.AccountIdentityKeyRotationSignature,
                UpdatedAt = u.AccountIdentityKeyUpdatedAt,
            })
            .FirstOrDefaultAsync();

        if (key is null) return Results.NotFound();

        // A missing key is 404 rather than a null-valued 200: "this account has not published one
        // yet" and "this account publishes an empty one" must not look the same to a peer deciding
        // whether it has anything to pin.
        return key.PublicKey is null or { Length: 0 } ? Results.NotFound() : Results.Ok(key);
    }

    /// <summary>
    /// Publishes the account identity key, or rotates it.
    ///
    /// <para><b>First publication is a land-grab, not a formality.</b> It used to require no
    /// password, write no audit row and emit no event, on the reasoning that there is nothing to
    /// invalidate - but per §I.2 no account in the field has a key, so <i>every</i> account was one
    /// stolen session token away from having its cryptographic identity chosen by somebody else.
    /// Whoever publishes first is who every peer TOFU-pins, and every device certificate that peers
    /// will ever verify chains to it. That is the same power a rotation confers, acquired for less,
    /// and invisibly. It now costs the same password, writes the same audit row and is broadcast the
    /// same way; the event carries <c>isFirstPublication</c> so a client can word it correctly.</para>
    ///
    /// <para>A <i>rotation</i> is a security event: it invalidates every peer's pinning and every
    /// device certificate issued under the old key. It costs the account password, is broadcast to
    /// every device, and lands in the append-only audit log.</para>
    ///
    /// <para><b>Stated honestly: the server cannot verify the recovery credential.</b> The contract
    /// asks rotation to require it, but the recovery key is derived from a passphrase the server
    /// never sees - so the password is the strongest thing checkable here. The check that actually
    /// protects peers is <c>rotationSignature</c>, made by the outgoing key and verified by them.
    /// A rotation that arrives without one is accepted and flagged, because the legitimate
    /// lost-every-device case cannot produce one either; peers then require an out-of-band
    /// re-verification rather than auto-accepting.</para>
    /// </summary>
    [WolverinePut("api/v1/users/identity-key")]
    public static async Task<(IResult, AccountIdentityKeyRotated?)> Put(
        PutAccountIdentityKeyDto dto,
        [NotBody] ClaimsPrincipal principal,
        [NotBody] IAccountPasswordVerifier passwords,
        [NotBody] SessionDeviceResolver sessionDevices,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        if (dto.PublicKey is null or { Length: 0 })
            return (Results.BadRequest("publicKey is required"), null);

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return (Results.NotFound(), null);

        var isFirstPublication = user.AccountIdentityPublicKey is null or { Length: 0 };

        if (!isFirstPublication && user.AccountIdentityPublicKey!.AsSpan().SequenceEqual(dto.PublicKey))
            return (Results.Ok(Describe(user)), null);

        // Monotonic, so a peer can tell a rotation from a replay of a superseded key.
        if (dto.Version <= user.AccountIdentityKeyVersion)
        {
            return (Results.Conflict(Describe(user)), null);
        }

        // Both paths, not just rotation. See the remarks: publishing first is the same power as
        // replacing, and it was the cheaper of the two to steal.
        var check = await passwords.CheckAsync(user, dto.Password);
        if (!check.IsOk())
        {
            return (Results.BadRequest(check.Describe(isFirstPublication
                ? "Publishing the account identity key"
                : "Rotating the account identity key")), null);
        }

        var now = DateTimeOffset.UtcNow;
        var previousVersion = user.AccountIdentityKeyVersion;

        // Recorded from the session, not from the body. `dto.DeviceId` is a string the caller
        // writes; an audit row naming a device chosen by whoever performed the action is not
        // evidence of anything.
        var actingDeviceId = (await sessionDevices.ResolveAsync(principal, userId))?.ClientDeviceId;

        user.AccountIdentityPublicKey = dto.PublicKey;
        user.AccountIdentityKeyVersion = dto.Version;
        user.AccountIdentityKeyRotationSignature = dto.RotationSignature;
        user.AccountIdentityKeyUpdatedAt = now;

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.AccountIdentityKeyRotated,
            ClientDeviceId = actingDeviceId,
            Detail = isFirstPublication
                ? $"first publication at v{dto.Version} - every peer will pin this key"
                : dto.RotationSignature is { Length: > 0 }
                    ? $"v{previousVersion} -> v{dto.Version}, signed by the outgoing key"
                    : $"v{previousVersion} -> v{dto.Version}, UNSIGNED - peers must re-verify out of band",
            CreatedAt = now,
        }));

        await ctx.SaveChangesAsync();

        return (Results.Ok(Describe(user)), new AccountIdentityKeyRotated
        {
            UserId = userId,
            PreviousVersion = previousVersion,
            Version = dto.Version,
            PublicKey = dto.PublicKey,
            SignedByOutgoingKey = dto.RotationSignature is { Length: > 0 },
            ChangedByDeviceId = actingDeviceId,
            IsFirstPublication = isFirstPublication,
            RotatedAt = now,
        });
    }

    private static AccountIdentityKeyDto Describe(ApplicationUser user) => new()
    {
        UserId = user.Id,
        PublicKey = user.AccountIdentityPublicKey,
        Version = user.AccountIdentityKeyVersion,
        RotationSignature = user.AccountIdentityKeyRotationSignature,
        UpdatedAt = user.AccountIdentityKeyUpdatedAt,
    };
}
