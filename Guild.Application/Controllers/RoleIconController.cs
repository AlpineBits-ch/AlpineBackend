using System.Security.Claims;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Domain.Validators;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using RoleAggregate = Guild.Domain.Aggregates.Role;
using RoleUpdatedEvent = Guild.Domain.Events.Role.RoleUpdated;

namespace Guild.Application.Controllers;

/// <summary>Upload, serve and remove a role's badge image.</summary>
[ApiController]
[Route("api/v1/guilds/{guildId}/roles/{roleId}/icon")]
public class RoleIconController(
    MicroserviceContext ctx,
    RoleIconService icons,
    GuildPermissionService permissionService,
    AuditLogService auditLog,
    MfaElevationService mfa,
    IMessageBus bus) : ControllerBase
{
    /// <summary>Matches <see cref="GuildIconController"/>'s set.</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    /// <summary>Discord's role icon limit, adopted verbatim.</summary>
    private const long MaxIconBytes = 256 * 1024;

    [HttpGet]
    public async Task<IActionResult> GetRoleIcon(string guildId, string roleId)
    {
        var hasIcon = await ctx.Roles
            .AsNoTracking()
            .AnyAsync(r => r.Id == roleId && r.GuildId == guildId && r.IconUrl != null);

        if (!hasIcon) return NotFound();

        return Redirect(icons.GetPresignedUrl(guildId, roleId));
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> UploadRoleIcon(string guildId, string roleId, [FromForm] IFormFile file)
    {
        var gate = await AuthorizeRoleEditAsync(guildId, roleId);
        if (gate.Failure is not null) return gate.Failure;
        var role = gate.Role!;

        if (file is null || file.Length == 0) return BadRequest("A file is required.");
        if (file.Length > MaxIconBytes) return BadRequest($"File exceeds the {MaxIconBytes / 1024}KB limit.");
        if (!AllowedContentTypes.Contains(file.ContentType)) return BadRequest("Unsupported image type.");

        // SetBadge is the only way either half of the badge is written, and it refuses a role
        // holding both.
        var iconUrl = RoleIconService.PublicUrlFor(guildId, roleId);
        role.SetBadge(iconUrl, null);

        // Validated against the composed URL rather than trusted because it was composed here: it
        // is built from INSTANCE_URL, which an operator writes, and a deployment that sets that to
        // a hostname with no scheme would otherwise persist a URL no client can resolve.
        var validation = new RoleValidator().Validate(role);
        if (!validation.IsValid) return BadRequest(validation.Errors[0].ErrorMessage);

        await icons.UploadAsync(file, guildId, roleId);

        auditLog.Log(guildId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, AuditActionType.RoleUpdated, roleId,
            new { Changes = new[] { new { Field = nameof(RoleAggregate.IconUrl), New = iconUrl } } });

        // Not a Wolverine handler, so nothing wraps this in the transactional middleware and
        // nothing dispatches a cascaded event - both have to be done by hand.
        await ctx.SaveChangesAsync();

        await bus.PublishAsync(new RoleUpdatedEvent { RoleId = roleId, GuildId = guildId });

        return Ok(new { roleId, iconUrl });
    }

    [Authorize]
    [HttpDelete]
    public async Task<IActionResult> DeleteRoleIcon(string guildId, string roleId)
    {
        var gate = await AuthorizeRoleEditAsync(guildId, roleId);
        if (gate.Failure is not null) return gate.Failure;
        var role = gate.Role!;

        if (role.IconUrl is null) return NoContent();

        role.SetBadge(null, role.UnicodeEmoji);

        auditLog.Log(guildId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, AuditActionType.RoleUpdated, roleId,
            new { Changes = new[] { new { Field = nameof(RoleAggregate.IconUrl), New = (string?)null } } });

        await ctx.SaveChangesAsync();

        // After the commit: an object still in storage whose row no longer points at it is invisible,
        // whereas a row pointing at an object that has already been deleted is a broken image in
        // every client.
        await icons.DeleteAsync(guildId, roleId);

        await bus.PublishAsync(new RoleUpdatedEvent { RoleId = roleId, GuildId = guildId });

        return NoContent();
    }

    /// <summary>The whole gate a role edit passes, in the order the role patch applies it: caller
    /// resolved, role found in this guild, ManageRoles, MFA, hierarchy, not integration-owned.
    /// Returns the tracked role on success.</summary>
    private async Task<(IActionResult? Failure, RoleAggregate? Role)> AuthorizeRoleEditAsync(string guildId, string roleId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Unauthorized(), null);

        var role = await ctx.Roles.FirstOrDefaultAsync(r => r.Id == roleId && r.GuildId == guildId);
        if (role is null) return (NotFound(), null);

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageRoles))
            return (Forbid(), null);

        if (!await mfa.IsSatisfiedAsync(guildId, User))
            return (MfaElevationService.RejectionResult(), null);

        if (!await permissionService.CanManageRoleAsync(userId, guildId, roleId))
            return (Forbid(), null);

        if (!role.IsEditableByHumans)
            return (BadRequest("This role is managed by an integration and cannot be edited."), null);

        return (null, role);
    }
}
