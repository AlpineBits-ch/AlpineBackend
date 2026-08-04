using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Facet.Extensions;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Identity.Application.Dtos.Response;
using ApplicationUserDto = Identity.Application.Dtos.Response.ApplicationUserDto;

namespace Identity.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/users")]
public class UserController(MicroserviceContext ctx, ILogger<UserController> logger) : ControllerBase
{
    [HttpGet("self")]
    public async Task<IActionResult> GetSelfAsync([FromServices] Services.ConsentService consents)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var user = ctx.Users.FirstOrDefault(u => u.Id == userId);
        if (user is null) return Ok(null);

        var dto = user.ToFacet<ApplicationUser, ApplicationUserDto>();
        // Reshaped rather than copied: the facet excludes the flags enum so it can go out as a
        // name array. See ApplicationUserDto.Interests.
        dto.Interests = user.Interests.ToWire();

        // T0-1. Additive: the legacy userPreferences block above is untouched, and the enforced
        // values arrive under a new key. Read separately rather than Included, because the facet
        // excludes the navigation - and because an account that predates the table gets the
        // all-defaults record here rather than a null the client would have to interpret.
        var now = DateTimeOffset.UtcNow;
        var privacy = await ctx.UserPrivacySettings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        // Read through the T1-11 floors, same as GET /api/v1/privacy-settings - a minor's self
        // payload must not report a wider policy than the one actually in force, or the client will
        // render a control as "on" that every enforcement point treats as off.
        var isMinor = user.IsMinorAt(now, Env.Privacy.AgeOfMajority);
        dto.PrivacySettings = PrivacySettingsMapping.ToDto(MinorPrivacyFloors.Snapshot(
            privacy ?? UserPrivacySettings.CreateDefault(userId, now), isMinor));

        // T1-10. Additive, under its own key, and empty for an account that is up to date or for a
        // deployment that has published no legal documents at all.
        var outstanding = await consents.GetOutstandingAsync(userId, now);
        dto.ConsentRequired = outstanding.Select(OutstandingConsentDto.From).ToArray();

        return Ok(dto);
    }

    /// <summary>
    /// Records which halves of the product this account came for.
    ///
    /// <para>Answered once by the onboarding picker, and re-runnable from settings afterwards -
    /// which is why this is a PUT and why <see cref="ApplicationUser.OnboardedAt"/> is stamped
    /// only on the first successful write. An account that picked Isle alone and later takes up
    /// messaging comes back through here to say so, and re-stamping would quietly rewrite when it
    /// joined.</para>
    ///
    /// <para><b>The empty set is refused.</b> An account that wants neither half is a state the
    /// client's launch sequence has no answer for: it cannot decide whether a master key is owed,
    /// so it would either ask forever or never. Refusing here keeps that state unreachable rather
    /// than leaving every reader to invent a fallback.</para>
    /// </summary>
    [HttpPut("self/onboarding")]
    public async Task<IActionResult> UpdateOnboardingAsync(UpdateOnboardingDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        if (!UserInterestsExtensions.TryParseWire(dto.Interests, out var interests))
            return BadRequest("interests must be a non-empty array of known names (\"isle\", \"social\").");

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();

        user.Interests = interests;
        user.OnboardedAt ??= DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();

        return Ok(new
        {
            onboardedAt = user.OnboardedAt,
            interests = user.Interests.ToWire(),
        });
    }



    /// <summary>
    /// Uploads the wrapped master key.
    ///
    /// <para><b>The guard here used to be backwards.</b> It compared
    /// <c>user.EncryptedMasterKey?.Version == dto.Version</c> and refused only on a match - so a
    /// harmless idempotent re-post of the same envelope was rejected, while replacing the wrapped
    /// master key with a <i>different</i> one sailed through with no re-auth, no rate limit and no
    /// trace. Every backup sealed under the old key becomes unopenable when that happens, so a
    /// replacement now costs the account password and leaves an audit row.</para>
    ///
    /// <para><b>This writes the password wrapping only.</b> A master key wrapped solely under the
    /// password is destroyed by a password reset - see
    /// <see cref="ApplicationUser.RecoveryCodeWrappedMasterKey"/>. Clients should move to
    /// <c>PUT api/v1/backup/recovery-key</c>, which takes both wrappings, names the KDF and reports
    /// orphaned blobs. This route is kept because the shipped desktop client calls it, and it now
    /// says so in its response.</para>
    /// </summary>
    [HttpPost("master")]
    public async Task<IActionResult> UploadMasterKey(CreateMasterKeyDto dto,
        [FromServices] IAccountPasswordVerifier passwords,
        [FromServices] SessionDeviceResolver sessionDevices)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if(user is null) return NotFound();

        var current = user.EncryptedMasterKey;

        if (current is not null)
        {
            // Same version, same bytes: the client retried a request whose response it never saw.
            // Answering Ok converges instead of leaving it convinced the upload failed.
            if (current.Version == dto.Version && current.CipherText.AsSpan().SequenceEqual(dto.CipherText))
                return Ok();

            if (dto.Version < current.Version)
                return BadRequest($"Master key version must not go backwards; current version is {current.Version}.");

            // Same version, different bytes is either a re-wrap under a new password - which is what
            // rewrap-password exists for, and which that route gates on a credential plus a verifier
            // check - or a different master key wearing the current version's number, which makes
            // every blob sealed under it unopenable while claiming nothing changed. This route
            // cannot tell them apart, so it refuses both, exactly as PUT backup/recovery-key does.
            // It used to let the password alone carry it, which meant the two routes disagreed about
            // the same write.
            if (current.Version == dto.Version)
            {
                return BadRequest(
                    "The master key wrapping differs from the stored one at the same version. Use "
                    + "POST api/v1/backup/recovery-key/rewrap-password to re-wrap under a new "
                    + "password, or bump the version to rotate the master key.");
            }

            var check = await passwords.CheckAsync(user, dto.Password);
            if (!check.IsOk()) return BadRequest(check.Describe("Replacing an existing master key"));

            logger.LogWarning("Master key replaced for {UserId}: version {Old} -> {New}",
                userId, current.Version, dto.Version);
        }

        user.EncryptedMasterKey = new EncryptedMasterKey()
        {
            Argon2Iterations = dto.Argon2Iterations,
            Salt = dto.Salt,
            Iv = dto.Iv,
            CipherText = dto.CipherText,
            Argon2Memory = dto.Argon2Memory,
            Argon2Parallelism = dto.Argon2Parallelism,
            Version = dto.Version,
            Kdf = dto.Kdf,
            PublicVerifier = dto.PublicVerifier,
        };

        // A freshly written password wrapping supersedes any stale stamp from an earlier reset.
        user.MasterKeyPasswordWrappingInvalidatedAt = null;

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.RecoveryKeyWritten,
            // Session-derived, never the header: an audit row naming a device the actor chose is
            // not evidence.
            ClientDeviceId = (await sessionDevices.ResolveAsync(User, userId))?.ClientDeviceId,
            Detail = $"master key version {current?.Version.ToString() ?? "none"} -> {dto.Version}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }));

        await ctx.SaveChangesAsync();

        // Told, not assumed. A client that only ever calls this route has an account whose master
        // key one password reset can destroy, and it has no other way to find that out.
        return Ok(new
        {
            version = dto.Version,
            hasRecoveryCodeWrapping = user.RecoveryCodeWrappedMasterKey is not null,
            encryptedHistoryRecoverable = user.EncryptedHistoryRecoverable,
        });
    }
    
    
    [HttpGet("self/settings")]
    public async Task<IActionResult> GetSettingsAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        
        var user = ctx.Users.FirstOrDefault(u => u.Id == userId);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(user.JsonSettings))
        {
            user.JsonSettings = "{}";
            await ctx.SaveChangesAsync();
        }
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(user.JsonSettings);
    
        return Ok(jsonElement);
    }
    
    /// <summary>
    /// Stores an opaque blob of <b>client-owned UI state with no server semantics</b>.
    ///
    /// <para><b>Nothing privacy-relevant may live here.</b> The server never reads this document,
    /// never validates its shape beyond the limits below, and never enforces anything in it - so a
    /// control stored here is a control that does not exist. Anything that must be enforced belongs
    /// on <c>UserPrivacySettings</c> and goes through <c>api/v1/privacy-settings</c>.</para>
    ///
    /// <para>The limits (T0-6) exist because this used to accept an arbitrary <c>JsonElement</c> of
    /// any size, any shape and any depth, and store it forever on a row every self-payload read
    /// loads: an unbounded blob per account is a storage-cost amplifier and a deserialization
    /// hazard, a non-object root breaks every client that expects to merge keys into it, and a
    /// deeply nested document is a stack-overflow shaped like a settings write.</para>
    /// </summary>
    [HttpPut("self/settings")]
    public async Task<IActionResult> GetSettingsAsync([FromBody] JsonElement body)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();

        // Shape and size are checked before the row is even looked up: neither depends on the user,
        // and a refused write should not cost a query.
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest("Settings must be a JSON object.");

        string rawJson = body.GetRawText();

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(rawJson);
        if (byteCount > MaxJsonSettingsBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Settings must serialize to at most {MaxJsonSettingsBytes} bytes; this document is {byteCount}.");
        }

        if (JsonDepth(body) > MaxJsonSettingsDepth)
            return BadRequest($"Settings must not nest more than {MaxJsonSettingsDepth} levels deep.");

        var user = ctx.Users.FirstOrDefault(u => u.Id == userId);
        if (user == null)
        {
            return NotFound();
        }

        user.JsonSettings = rawJson;
        await ctx.SaveChangesAsync();
        return Ok(user.JsonSettings);
    }

    /// <summary>16 KB of client UI state. Generous for what this is for (panel sizes, collapsed
    /// sections, last-opened ids) and small enough that a million accounts is megabytes.</summary>
    public const int MaxJsonSettingsBytes = 16 * 1024;

    /// <summary>Nesting cap. Well under System.Text.Json's own 64-level default, so the limit that
    /// applies is this one - stated - rather than the serializer's, discovered.</summary>
    public const int MaxJsonSettingsDepth = 16;

    /// <summary>
    /// Depth of a parsed document, counting the root object as level 1.
    ///
    /// <para>Iterative rather than recursive on purpose: a recursive walk over a document whose depth
    /// is the thing being checked overflows the stack on exactly the input the check exists to
    /// refuse. The parser's own <c>MaxDepth</c> has already bounded this to 64 by the time we get
    /// here, but that is a property of the pipeline's configuration, not of this method.</para>
    /// </summary>
    public static int JsonDepth(JsonElement root)
    {
        var deepest = 0;
        var pending = new Stack<(JsonElement Element, int Depth)>();
        pending.Push((root, 1));

        while (pending.Count > 0)
        {
            var (element, depth) = pending.Pop();
            if (depth > deepest) deepest = depth;

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        pending.Push((property.Value, depth + 1));
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        pending.Push((item, depth + 1));
                    break;
            }
        }

        return deepest;
    }

    /// <summary>
    /// Registers (or re-points) one push endpoint for the caller. Supersedes the two
    /// transport-specific endpoints below, which now delegate here.
    /// </summary>
    [HttpPost("self/push-token")]
    public async Task<IActionResult> CreatePushTokenAsync(CreatePushTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, dto.Kind, dto.DeviceId);
    }

    /// <summary>Deprecated - POST self/push-token with <c>kind: "Fcm"</c>.</summary>
    [HttpPost("self/device-token")]
    public async Task<IActionResult> CreateDeviceTokenAsync(CreateDeviceTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, PushTokenKind.Fcm, dto.DeviceId);
    }

    /// <summary>Deprecated - POST self/push-token with <c>kind: "ApnsVoip"</c>.</summary>
    [HttpPost("self/voip-token")]
    public async Task<IActionResult> CreateVoipTokenAsync(CreateDeviceTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, PushTokenKind.ApnsVoip, dto.DeviceId);
    }

    /// <summary>
    /// Upsert rather than insert-if-absent: the (kind, token) pair is unique across the table, and
    /// both push providers hand the same token to a different account after a reinstall or an
    /// account switch on the same handset. Inserting blindly would violate the index; skipping
    /// would leave the handset's notifications going to whoever registered it first.
    /// </summary>
    private async Task<IActionResult> UpsertPushTokenAsync(string userId, string token, PushTokenKind kind, string? clientDeviceId)
    {
        string? deviceRowId = null;
        if (!string.IsNullOrWhiteSpace(clientDeviceId))
        {
            deviceRowId = await ctx.UserDevices
                .Where(d => d.UserId == userId && d.ClientDeviceId == clientDeviceId)
                .Select(d => d.Id)
                .FirstOrDefaultAsync();

            // An unknown device id is a client bug worth surfacing, but not worth losing the token
            // over - register it unattached rather than dropping the registration.
            if (deviceRowId is null)
            {
                logger.LogWarning("Push token registered by user {UserId} for unknown device {ClientDeviceId}",
                    userId, clientDeviceId);
            }
        }

        var existing = await ctx.UserPushTokens.FirstOrDefaultAsync(t => t.Kind == kind && t.Token == token);
        if (existing is not null)
        {
            existing.ReassignTo(userId, deviceRowId);
            await ctx.SaveChangesAsync();
            return Accepted();
        }

        ctx.UserPushTokens.Add(UserPushToken.Create(new CreateUserPushTokenParams
        {
            UserId = userId,
            Token = token,
            Kind = kind,
            DeviceId = deviceRowId,
        }));
        await ctx.SaveChangesAsync();
        return Created();
    }

    /// <summary>Lets a client drop its own endpoint on sign-out instead of leaving a token that
    /// keeps ringing a handset nobody is signed in on.</summary>
    [HttpDelete("self/push-token")]
    public async Task<IActionResult> DeletePushTokenAsync([FromQuery] string token, [FromQuery] PushTokenKind? kind)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(token)) return BadRequest("token is required.");

        var rows = await ctx.UserPushTokens
            .Where(t => t.UserId == userId && t.Token == token && (kind == null || t.Kind == kind))
            .ToListAsync();

        if (rows.Count == 0) return NotFound();

        ctx.UserPushTokens.RemoveRange(rows);
        await ctx.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Starts the grace-period countdown rather than deleting anything immediately -
    /// see ApplicationUser.RequestDeletion. Login is blocked from this point on
    /// (IsSigninAllowed), but the request is reversible via self/cancel-deletion until the
    /// purge sweep picks it up.</summary>
    [HttpDelete("self")]
    public async Task<IActionResult> RequestDeletionAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();

        var purgeScheduledAt = DateTimeOffset.UtcNow.Add(Env.AccountDeletion.GracePeriod);
        user.RequestDeletion(purgeScheduledAt);
        await ctx.SaveChangesAsync();

        return Ok(new { purgeScheduledAt });
    }

    [HttpPost("self/cancel-deletion")]
    public async Task<IActionResult> CancelDeletionAsync()
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();

        if (!user.CancelDeletionRequest())
            return Conflict("Account is not pending deletion, or the purge has already started.");

        await ctx.SaveChangesAsync();
        return Ok();
    }
}