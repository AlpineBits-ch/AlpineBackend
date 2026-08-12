using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Facet.Extensions;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Contracts.Push;
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

        // T0-1.
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

    /// <summary>Records which halves of the product this account came for.</summary>
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



    /// <summary>Uploads the wrapped master key.</summary>
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
            if (current.Version == dto.Version && current.CipherText.AsSpan().SequenceEqual(dto.CipherText))
                return Ok();

            if (dto.Version < current.Version)
                return BadRequest($"Master key version must not go backwards; current version is {current.Version}.");

            // Same version, different bytes is either a re-wrap under a new password - which is
            // what rewrap-password exists for, and which that route gates on a credential plus a
            // verifier check - or a different master key wearing the current version's number,
            // which makes every blob sealed under it unopenable while claiming nothing changed.
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

        // Told, not assumed.
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
    
    /// <summary>Stores an opaque blob of client-owned UI state with no server semantics.</summary>
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

    /// <summary>16 KB of client UI state.</summary>
    public const int MaxJsonSettingsBytes = 16 * 1024;

    /// <summary>Nesting cap.</summary>
    public const int MaxJsonSettingsDepth = 16;

    /// <summary>Depth of a parsed document, counting the root object as level 1.</summary>
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

    /// <summary>Registers (or re-points) one push endpoint for the caller.</summary>
    [HttpPost("self/push-token")]
    public async Task<IActionResult> CreatePushTokenAsync(CreatePushTokenDto dto)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        // A browser has no token, so the addressable value arrives as `endpoint` instead.
        if (dto.Kind == PushTokenKind.WebPush)
        {
            var problem = WebPushSubscription.Validate(dto.Endpoint, dto.P256dh, dto.Auth);
            if (problem is not null) return BadRequest(problem);

            return await UpsertPushTokenAsync(userId, dto.Endpoint!, dto.Kind, dto.DeviceId,
                dto.P256dh, dto.Auth);
        }

        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest("token is required.");

        return await UpsertPushTokenAsync(userId, dto.Token, dto.Kind, dto.DeviceId);
    }

    /// <summary>
    /// The VAPID public key a browser needs as <c>applicationServerKey</c> before it can subscribe.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("push/vapid-public-key")]
    public IActionResult GetVapidPublicKey() =>
        Env.Vapid.IsConfigured
            ? Ok(new VapidPublicKeyDto { PublicKey = Env.Vapid.PublicKey })
            : NotFound();

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
    /// account switch on the same handset.
    /// </summary>
    private async Task<IActionResult> UpsertPushTokenAsync(
        string userId,
        string token,
        PushTokenKind kind,
        string? clientDeviceId,
        string? p256dh = null,
        string? auth = null)
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
            // The keys go along for a Web Push re-registration: a browser re-subscribing against the
            // same endpoint rotates p256dh and auth, and keeping the old pair leaves the endpoint
            // reachable and every payload sent to it undecryptable.
            existing.ReassignTo(userId, deviceRowId, p256dh, auth);
            await ctx.SaveChangesAsync();
            return Accepted();
        }

        ctx.UserPushTokens.Add(UserPushToken.Create(new CreateUserPushTokenParams
        {
            UserId = userId,
            Token = token,
            Kind = kind,
            DeviceId = deviceRowId,
            P256dh = p256dh,
            Auth = auth,
        }));
        await ctx.SaveChangesAsync();
        return Created();
    }

    /// <summary>Lets a client drop its own endpoint on sign-out instead of leaving a token that
    /// keeps ringing a handset nobody is signed in on.</summary>
    /// <param name="token">The FCM/APNs token to drop.</param>
    /// <param name="kind">Narrows the delete to one transport. Absent means every transport holding
    /// this value.</param>
    /// <param name="endpoint">A Web Push subscription's endpoint - an alias for
    /// <paramref name="token"/>, because that is the name a browser knows the value by and a client
    /// holding a <c>PushSubscription</c> has no "token" to send. Either spelling deletes the same
    /// row.</param>
    [HttpDelete("self/push-token")]
    public async Task<IActionResult> DeletePushTokenAsync(
        [FromQuery] string? token,
        [FromQuery] PushTokenKind? kind,
        [FromQuery] string? endpoint = null)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var value = string.IsNullOrWhiteSpace(token) ? endpoint : token;
        if (string.IsNullOrWhiteSpace(value)) return BadRequest("token or endpoint is required.");

        var rows = await ctx.UserPushTokens
            .Where(t => t.UserId == userId && t.Token == value && (kind == null || t.Kind == kind))
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