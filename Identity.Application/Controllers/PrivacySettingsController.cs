using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Identity.Application.Dtos.Response;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Identity.Application.Controllers;

/// <summary>
/// The account's own privacy record - the one writable, cross-service-readable settings surface.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/privacy-settings")]
public class PrivacySettingsController(
    MicroserviceContext ctx,
    IMessageBus bus,
    ILogger<PrivacySettingsController> logger) : ControllerBase
{
    /// <summary>
    /// Returns the caller's privacy record, minting the default one if the account somehow has
    /// none.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<UserPrivacySettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var settings = await ResolveAsync(userId, persistIfCreated: true);
        if (settings is null) return NotFound();

        // Reported through the T1-11 floors.
        var isMinor = await IsMinorAsync(userId);
        return Ok(PrivacySettingsMapping.ToDto(MinorPrivacyFloors.Snapshot(settings, isMinor)));
    }

    /// <summary>Partial update.</summary>
    [HttpPatch]
    [ProducesResponseType<UserPrivacySettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PatchAsync([FromBody] JsonElement body)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var settings = await ResolveAsync(userId, persistIfCreated: false);
        if (settings is null) return NotFound();

        var isMinor = await IsMinorAsync(userId);

        var result = PrivacySettingsPatch.Apply(body, settings, isMinor);

        // A floor breach is a 403 with a machine-readable code naming the field, not a 400 (T1-11).
        if (result.RestrictedField is not null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = MinorPrivacyFloors.RestrictionCode,
                field = result.RestrictedField,
                message = result.Error,
            });
        }

        if (!result.Ok) return BadRequest(result.Error);

        if (result.ChangedFields.Count == 0)
        {
            // Still persist the row if resolving it had to mint one - otherwise the very first GET
            // after a PATCH{} would mint it again and the account would never acquire a record.
            await ctx.SaveChangesAsync();
            return Ok(PrivacySettingsMapping.ToDto(MinorPrivacyFloors.Snapshot(settings, isMinor)));
        }

        settings.Version++;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.PrivacySettingsChanged,
            // Field names only.
            Detail = $"v{settings.Version}: {string.Join(", ", result.ChangedFields)}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }));

        await ctx.SaveChangesAsync();

        // After the commit, never before.
        await bus.PublishAsync(new UserPrivacySettingsChangedEvent
        {
            UserId = userId,
            Version = settings.Version,
        });

        logger.LogInformation("Privacy settings updated for {UserId} to version {Version}: {Fields}",
            userId, settings.Version, string.Join(", ", result.ChangedFields));

        return Ok(PrivacySettingsMapping.ToDto(MinorPrivacyFloors.Snapshot(settings, isMinor)));
    }

    /// <summary>Whether the caller is below the age of majority right now (T1-11).</summary>
    private async Task<bool> IsMinorAsync(string userId)
    {
        var birthDate = await ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (DateOnly?)u.AgeVerification.BirthDate)
            .FirstOrDefaultAsync();

        if (birthDate is null) return false;

        return new AgeVerification { BirthDate = birthDate.Value }
            .IsMinorAt(DateTimeOffset.UtcNow, Env.Privacy.AgeOfMajority);
    }

    /// <summary>
    /// The caller's settings row, tracked, minting the all-defaults one if the account has none.
    /// </summary>
    /// <param name="persistIfCreated">
    /// False when the caller is about to commit anyway, so a minted row is not written twice.
    /// </param>
    private async Task<UserPrivacySettings?> ResolveAsync(string userId, bool persistIfCreated)
    {
        var settings = await ctx.UserPrivacySettings.FirstOrDefaultAsync(p => p.UserId == userId);
        if (settings is not null) return settings;

        if (!await ctx.Users.AnyAsync(u => u.Id == userId)) return null;

        settings = UserPrivacySettings.CreateDefault(userId, DateTimeOffset.UtcNow);
        ctx.UserPrivacySettings.Add(settings);
        if (persistIfCreated) await ctx.SaveChangesAsync();
        return settings;
    }
}
