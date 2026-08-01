using System.Security.Claims;
using Identity.Application.Dtos.Request;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>The account's long-lived Ed25519 identity key - public half only.</summary>
[Authorize]
public class AccountIdentityKeyEndpoint
{
    /// <summary>A user's account identity key, for TOFU-pinning by peers.</summary>
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

    /// <summary>Publishes the account identity key, or rotates it.</summary>
    [WolverinePut("api/v1/users/identity-key")]
    public static async Task<(IResult, AccountIdentityKeyRotated?)> Put(
        PutAccountIdentityKeyDto dto,
        [NotBody] ClaimsPrincipal principal,
        [NotBody] UserManager<ApplicationUser> users,
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

        if (!isFirstPublication &&
            (string.IsNullOrEmpty(dto.Password) || !await users.CheckPasswordAsync(user, dto.Password)))
        {
            return (Results.BadRequest("Rotating the account identity key requires the account password."), null);
        }

        var now = DateTimeOffset.UtcNow;
        var previousVersion = user.AccountIdentityKeyVersion;

        user.AccountIdentityPublicKey = dto.PublicKey;
        user.AccountIdentityKeyVersion = dto.Version;
        user.AccountIdentityKeyRotationSignature = dto.RotationSignature;
        user.AccountIdentityKeyUpdatedAt = now;

        if (!isFirstPublication)
        {
            ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
            {
                UserId = userId,
                Action = IdentityAuditActions.AccountIdentityKeyRotated,
                ClientDeviceId = dto.DeviceId,
                Detail = dto.RotationSignature is { Length: > 0 }
                    ? $"v{previousVersion} -> v{dto.Version}, signed by the outgoing key"
                    : $"v{previousVersion} -> v{dto.Version}, UNSIGNED - peers must re-verify out of band",
                CreatedAt = now,
            }));
        }

        await ctx.SaveChangesAsync();

        return (Results.Ok(Describe(user)), isFirstPublication
            ? null
            : new AccountIdentityKeyRotated
            {
                UserId = userId,
                PreviousVersion = previousVersion,
                Version = dto.Version,
                PublicKey = dto.PublicKey,
                SignedByOutgoingKey = dto.RotationSignature is { Length: > 0 },
                ChangedByDeviceId = dto.DeviceId,
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
