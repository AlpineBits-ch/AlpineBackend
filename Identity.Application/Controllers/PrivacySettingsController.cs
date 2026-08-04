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
///
/// <para>Separate from <c>UserController</c>'s preferences block on purpose: these have their own
/// defaults, their own change event and their own audit action, and folding them into the self
/// payload's <c>userPreferences</c> object would have made every one of those a property of a type
/// that means something else. <c>GET /users/self</c> still carries a read-only copy under
/// <c>privacySettings</c> so a client needs one round trip at launch, not two.</para>
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
    /// Returns the caller's privacy record, minting the default one if the account somehow has none.
    ///
    /// <para>Minting on read rather than 404-ing: an account created before this table existed and
    /// missed the backfill would otherwise have a settings endpoint that permanently 404s, and the
    /// client's only recovery would be a write it has nothing to base on. The row it mints is the
    /// same all-defaults row <c>ApplicationUser.Create</c> would have made, so nothing is invented.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType<UserPrivacySettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var settings = await ResolveAsync(userId, persistIfCreated: true);
        if (settings is null) return NotFound();

        // Reported through the T1-11 floors. A row that holds a wider value than a minor may hold -
        // written before the account's birth date said what it says now, or by a path that predates
        // the floors - must not be reported as though it were in effect, because every consuming
        // service reads its own copy of this record and would act on it.
        var isMinor = await IsMinorAsync(userId);
        return Ok(PrivacySettingsMapping.ToDto(MinorPrivacyFloors.Snapshot(settings, isMinor)));
    }

    /// <summary>
    /// Partial update. Every field is optional; an omitted field is left alone, and an unknown field
    /// is a <c>400</c> rather than a silently dropped key - see <see cref="PrivacySettingsPatch"/>
    /// for why that distinction is the whole point of this endpoint.
    ///
    /// <para>A write that changes nothing - <c>{}</c>, or a re-post of the current state - returns
    /// the current record without bumping <c>Version</c>, without an audit row and without a change
    /// event. Every consuming service treats that event as "drop your cache and re-ask", so emitting
    /// one for a no-op would turn a client's idle re-sync into a fleet-wide cache stampede.</para>
    /// </summary>
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
        // The request is well-formed; it is the account that may not make it, and a client that reads
        // "bad request" will keep re-sending the same well-formed body forever.
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
            // Field names only. Which controls someone changed is the audit; what they changed them
            // to is the setting itself, and copying values here would put a second, never-purged
            // record of the user's contactability in a table that is deliberately append-only.
            Detail = $"v{settings.Version}: {string.Join(", ", result.ChangedFields)}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }));

        await ctx.SaveChangesAsync();

        // After the commit, never before. A consuming service that evicts its cache on this event and
        // re-reads before the transaction lands would cache the pre-write state and hold it until the
        // next change - which for a "block all DMs" toggle is exactly the window that matters.
        await bus.PublishAsync(new UserPrivacySettingsChangedEvent
        {
            UserId = userId,
            Version = settings.Version,
        });

        logger.LogInformation("Privacy settings updated for {UserId} to version {Version}: {Fields}",
            userId, settings.Version, string.Join(", ", result.ChangedFields));

        return Ok(PrivacySettingsMapping.ToDto(MinorPrivacyFloors.Snapshot(settings, isMinor)));
    }

    /// <summary>
    /// Whether the caller is below the age of majority right now (T1-11).
    ///
    /// <para>Resolved from the stored birth date on every request rather than cached anywhere. That
    /// is the whole mechanism behind "re-evaluate on birthday rollover": on the morning the account
    /// turns eighteen this returns false, the floors stop applying, and the settings the user
    /// originally chose are theirs again - unlocked, not silently overwritten while they were a minor
    /// and left that way afterwards.</para>
    /// </summary>
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
    /// Returns null only when the account itself does not exist - for an authenticated caller that
    /// means the token outlived the user.
    /// </summary>
    /// <param name="persistIfCreated">False when the caller is about to commit anyway, so a minted
    /// row is not written twice.</param>
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
