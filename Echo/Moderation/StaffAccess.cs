using System.Security.Claims;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Persistence.Persistance;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Echo.Moderation;

/// <summary>Who the caller is, once their staff tier has been resolved.</summary>
public sealed record StaffPrincipal(string UserId, StaffRole Role, string? UserName)
{
    public bool IsAdmin => Role == StaffRole.Admin;
}

/// <summary>Resolves the caller's staff tier, on every request, from Identity.</summary>
public class StaffAccess(IMessageBus bus, ILogger<StaffAccess> logger)
{
    public async Task<StaffPrincipal?> ResolveAsync(HttpContext context)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId)) return null;

        try
        {
            var response = await bus.InvokeAsync<IsUserAdministrativeResponse>(
                new IsUserAdministrativeRequest { UserId = userId });

            var role = response.Role switch
            {
                "Admin" => StaffRole.Admin,
                "Moderator" => StaffRole.Moderator,
                // Falls back to the boolean for the window in which an older Identity build is still
                // deployed beside a newer gateway: it answers IsAdministrative and leaves Role at its
                // default. Without this a rolling deploy locks every admin out of the console.
                _ => response.IsAdministrative ? StaffRole.Admin : StaffRole.None,
            };

            if (role == StaffRole.None)
            {
                logger.LogWarning("Non-staff account {UserId} attempted to reach the moderation console", userId);
                return null;
            }

            return new StaffPrincipal(userId, role, response.UserName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Staff check failed for {UserId}; denying access.", userId);
            return null;
        }
    }
}

/// <summary>
/// Shared plumbing for the four admin controllers: the staff gate, the audit write, and the two
/// refusal shapes.
/// </summary>
[ApiController]
public abstract class AdminControllerBase(MicroserviceContext context, StaffAccess staff) : ControllerBase
{
    protected MicroserviceContext Db { get; } = context;

    /// <summary>The caller, or null.</summary>
    protected Task<StaffPrincipal?> ResolveStaffAsync() => staff.ResolveAsync(HttpContext);

    /// <summary>A 403 with a machine-readable code, rather than <c>Forbid()</c>.</summary>
    protected IActionResult StaffForbidden() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "staff_required",
            message = "This endpoint requires a moderator or administrator account.",
        });

    protected IActionResult AdminOnly() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "admin_required",
            message = "This endpoint requires an administrator account.",
        });

    protected IActionResult Failure(int status, string code, string message) =>
        StatusCode(status, new { code, message });

    /// <summary>Records a staff mutation.</summary>
    protected void Audit(StaffPrincipal actor, string action, string? subjectId, string? detail = null)
    {
        Db.ModerationAuditEntries.Add(ModerationAuditEntry.Create(
            actor.UserId, action, subjectId, detail,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            DateTimeOffset.UtcNow));
    }

    /// <summary>Page size, clamped.</summary>
    protected static (int Limit, int Offset) Page(int limit, int offset) =>
        (Math.Clamp(limit <= 0 ? 25 : limit, 1, 100), Math.Max(0, offset));
}
