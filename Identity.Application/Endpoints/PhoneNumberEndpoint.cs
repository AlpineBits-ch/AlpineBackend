using System.Security.Claims;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>The account's own phone number: set it, replace it, remove it.</summary>
[Authorize]
public class PhoneNumberEndpoint
{
    /// <summary>Records or replaces the caller's own number.</summary>
    [WolverinePut("api/v1/users/self/phone")]
    public static async Task<IResult> Put(
        SetPhoneNumberDto dto,
        [NotBody] ClaimsPrincipal principal,
        [NotBody] SessionDeviceResolver sessionDevices,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var normalized = E164PhoneNumber.Normalize(dto.PhoneNumber);
        if (normalized is null)
        {
            // The message names the format rather than the offending character.
            return Results.BadRequest(
                "A phone number must be in international format, starting with + and the country "
                + "code, for example +41791234567.");
        }

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Results.NotFound();

        var previous = user.PhoneNumber;
        if (string.Equals(previous, normalized, StringComparison.Ordinal))
        {
            return Results.Ok(new { PhoneNumber = normalized });
        }

        user.PhoneNumber = normalized;

        // PhoneNumberConfirmed is ASP.NET Identity's own flag and stays false for the same reason
        // PhoneVerifiedAt stays null: nothing confirmed anything.
        user.PhoneNumberConfirmed = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        var device = await sessionDevices.ResolveAsync(principal, userId);

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.PhoneNumberChanged,
            ClientDeviceId = device?.ClientDeviceId,
            // Masked on both sides.
            Detail = previous is null
                ? $"set to {E164PhoneNumber.Mask(normalized)}"
                : $"{E164PhoneNumber.Mask(previous)} -> {E164PhoneNumber.Mask(normalized)}",
            CreatedAt = DateTimeOffset.UtcNow,
        }));

        await ctx.SaveChangesAsync();

        return Results.Ok(new { PhoneNumber = normalized });
    }

    /// <summary>Removes the caller's own number.</summary>
    [WolverineDelete("api/v1/users/self/phone")]
    public static async Task<IResult> Delete(
        [NotBody] ClaimsPrincipal principal,
        [NotBody] SessionDeviceResolver sessionDevices,
        [NotBody] IMessageBus bus,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Results.NotFound();

        if (user.PhoneNumber is null)
        {
            // Still published, even though nothing changed here.
            await bus.PublishAsync(new UserPhoneNumberRemovedEvent { UserId = userId });
            return Results.NoContent();
        }

        var previous = user.PhoneNumber;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        var device = await sessionDevices.ResolveAsync(principal, userId);

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.PhoneNumberRemoved,
            ClientDeviceId = device?.ClientDeviceId,
            Detail = $"removed {E164PhoneNumber.Mask(previous)}",
            CreatedAt = DateTimeOffset.UtcNow,
        }));

        await ctx.SaveChangesAsync();

        await bus.PublishAsync(new UserPhoneNumberRemovedEvent { UserId = userId });

        return Results.NoContent();
    }
}
