using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Echo.Controllers.Admin;

/// <summary>User lookup and the actions taken against accounts.</summary>
[Authorize]
[Route("api/v1/admin")]
public class AdminUsersController(
    MicroserviceContext context,
    StaffAccess staff,
    IMessageBus bus,
    ModerationMailer mailer,
    ILogger<AdminUsersController> logger)
    : AdminControllerBase(context, staff)
{
    /// <summary>Who the console is signed in as.</summary>
    [HttpGet("session")]
    public async Task<IActionResult> SessionAsync()
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Ok(new StaffSessionDto
        {
            UserId = actor.UserId,
            UserName = actor.UserName,
            Role = actor.Role.ToString(),
            CanViewAudit = actor.IsAdmin,
        });
    }

    /// <summary>Instance numbers: Identity's user counts, plus the queue depths counted here.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> StatsAsync(CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var now = DateTimeOffset.UtcNow;
        var stale = now.AddHours(-48);

        var reports = Db.ModerationReports.AsNoTracking();
        var openReports = reports.Where(r => r.Status == ReportStatus.Open || r.Status == ReportStatus.Triaged);

        var local = new ModerationStatsDto
        {
            OpenReports = await openReports.CountAsync(ct),
            CriticalReports = await openReports.CountAsync(r => r.Priority == ReportPriority.Critical, ct),
            HighReports = await openReports.CountAsync(r => r.Priority == ReportPriority.High, ct),
            UnassignedReports = await openReports.CountAsync(r => r.AssignedToUserId == null, ct),
            StaleReports = await openReports.CountAsync(r => r.CreatedAt < stale, ct),

            OpenAppeals = await Db.ModerationAppeals.AsNoTracking()
                .CountAsync(a => a.Status == AppealStatus.Pending || a.Status == AppealStatus.UnderReview, ct),

            OpenTickets = await Db.SupportTickets.AsNoTracking()
                .CountAsync(t => t.Status != SupportTicketStatus.Resolved && t.Status != SupportTicketStatus.Closed, ct),

            TicketsAwaitingStaff = await Db.SupportTickets.AsNoTracking()
                .CountAsync(t => t.Status == SupportTicketStatus.Open || t.Status == SupportTicketStatus.AwaitingStaff, ct),

            ActiveBans = await Db.ModerationActions.AsNoTracking().CountAsync(a =>
                a.Kind == ModerationActionKind.Ban && a.RevokedAt == null
                && (a.ExpiresAt == null || a.ExpiresAt > now), ct),

            ActiveSuspensions = await Db.ModerationActions.AsNoTracking().CountAsync(a =>
                a.Kind == ModerationActionKind.Suspension && a.RevokedAt == null && a.ExpiresAt > now, ct),
        };

        GetPlatformStatsResponse? platform = null;
        try
        {
            platform = await bus.InvokeAsync<GetPlatformStatsResponse>(new GetPlatformStatsRequest());
        }
        catch (Exception ex)
        {
            // Degraded rather than failed.
            logger.LogError(ex, "Platform statistics unavailable; serving the local counts only.");
        }

        return Ok(new { platform, moderation = local, platformAvailable = platform is not null });
    }

    [HttpGet("users")]
    public async Task<IActionResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] int limit,
        [FromQuery] int offset)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var (take, skip) = Page(limit, offset);

        var result = await bus.InvokeAsync<SearchUsersResponse>(new SearchUsersRequest
        {
            Query = q,
            Status = status,
            UserType = type,
            Limit = take,
            Offset = skip,
        });

        // The sanction flag is joined on locally rather than asked of Identity: Identity knows an
        // account is Banned, it does not know which action banned it or when that expires, and the
        // list is where a moderator decides whether to open a row at all.
        var ids = result.Users.Select(u => u.Id).ToList();
        var now = DateTimeOffset.UtcNow;

        var sanctioned = await Db.ModerationActions.AsNoTracking()
            .Where(a => ids.Contains(a.TargetUserId)
                        && (a.Kind == ModerationActionKind.Ban || a.Kind == ModerationActionKind.Suspension)
                        && a.RevokedAt == null
                        && (a.ExpiresAt == null || a.ExpiresAt > now))
            .Select(a => a.TargetUserId)
            .Distinct()
            .ToListAsync();

        var sanctionedSet = sanctioned.ToHashSet();

        return Ok(new
        {
            total = result.Total,
            users = result.Users.Select(u => new
            {
                u.Id, u.UserName, u.Email, u.Status, u.UserType, u.EmailVerified, u.CreatedAt,
                hasActiveSanction = sanctionedSet.Contains(u.Id),
            }),
        });
    }

    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUserAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var detail = await bus.InvokeAsync<GetAdminUserDetailResponse>(
            new GetAdminUserDetailRequest { UserId = id });

        if (detail.User is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        var actions = await Db.ModerationActions.AsNoTracking()
            .Where(a => a.TargetUserId == id)
            .OrderByDescending(a => a.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var against = await Db.ModerationReports.AsNoTracking()
            .Where(r => r.TargetUserId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        // Reports this account filed, not just ones against it.
        var filed = await Db.ModerationReports.AsNoTracking()
            .CountAsync(r => r.ReporterUserId == id, ct);

        return Ok(new
        {
            user = detail,
            actions = actions.Select(a => ActionDto.From(a, now)),
            reportsAgainst = against.Select(r => ReportDto.From(r)),
            reportsFiledCount = filed,
            activeSanction = actions.FirstOrDefault(a => a.IsActiveAt(now)) is { } live
                ? ActionDto.From(live, now)
                : null,
        });
    }

    /// <summary>Issues an action against an account.</summary>
    [HttpPost("users/{id}/actions")]
    public async Task<IActionResult> IssueAsync(
        string id, [FromBody] IssueActionRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        if (!Enum.IsDefined(request.Kind)) return Failure(400, "invalid_kind", "Unknown action kind.");
        if (!Enum.IsDefined(request.Reason)) return Failure(400, "invalid_reason", "Unknown reason.");

        if (id == actor.UserId)
            return Failure(400, "self_action", "You cannot action your own account.");

        var now = DateTimeOffset.UtcNow;

        DateTimeOffset? expiresAt = null;
        if (request.Kind == ModerationActionKind.Suspension)
        {
            if (request.DurationHours is not > 0)
                return Failure(400, "duration_required", "A suspension requires a positive duration in hours.");

            // Two years.
            if (request.DurationHours > 24 * 730)
                return Failure(400, "duration_too_long", "Use a ban rather than a suspension longer than two years.");

            expiresAt = now.AddHours(request.DurationHours.Value);
        }
        else if (request.Kind == ModerationActionKind.Ban && request.DurationHours is > 0)
        {
            expiresAt = now.AddHours(request.DurationHours.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.ReportId)
            && !await Db.ModerationReports.AnyAsync(r => r.Id == request.ReportId, ct))
        {
            return Failure(400, "report_missing", "No report with that id.");
        }

        // The staff-on-staff gate.
        var targetRole = await ResolveTargetRoleAsync(id);
        if (targetRole != StaffRole.None && !actor.IsAdmin)
            return AdminOnly();

        string? email = null;
        string? displayName = null;

        var restricts = request.Kind is ModerationActionKind.Ban or ModerationActionKind.Suspension;
        var lifts = request.Kind == ModerationActionKind.Unban;

        if (restricts || lifts)
        {
            SetUserModerationStatusResponse status;
            try
            {
                status = await bus.InvokeAsync<SetUserModerationStatusResponse>(
                    new SetUserModerationStatusRequest
                    {
                        UserId = id,
                        ActorUserId = actor.UserId,
                        Banned = restricts,
                        Reason = $"{request.Kind}: {request.Reason}",
                    });
            }
            catch (Exception ex)
            {
                // Nothing has been written yet, so this is a clean failure with no half-state.
                logger.LogError(ex, "Identity did not answer the status change for {UserId}", id);
                return Failure(503, "identity_unavailable",
                    "The account service did not answer. Nothing was changed - try again.");
            }

            if (!status.Success)
            {
                return Failure(
                    status.FailureCode == "not_found" ? 404 : 409,
                    status.FailureCode ?? "rejected",
                    status.FailureMessage ?? "The account service refused the change.");
            }

            email = status.Email;
            displayName = status.UserName;
        }
        else
        {
            // Warnings and notes change no status, so the address has to be looked up separately.
            try
            {
                var user = await bus.InvokeAsync<GetAdminUserDetailResponse>(
                    new GetAdminUserDetailRequest { UserId = id });

                if (user.User is null) return NotFound();

                email = user.User.Email;
                displayName = user.User.UserName;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve {UserId} for a {Kind}; proceeding without a notice.", id, request.Kind);
            }
        }

        var action = ModerationAction.Create(new CreateModerationActionParams
        {
            TargetUserId = id,
            ActorUserId = actor.UserId,
            Kind = request.Kind,
            Reason = request.Reason,
            PublicNote = request.PublicNote,
            InternalNote = request.InternalNote,
            ExpiresAt = expiresAt,
            ReportId = string.IsNullOrWhiteSpace(request.ReportId) ? null : request.ReportId,
        }, now);

        Db.ModerationActions.Add(action);

        // An unban ends whatever was in force, so the old row stops reading as active.
        if (lifts)
        {
            var live = await Db.ModerationActions
                .Where(a => a.TargetUserId == id
                            && (a.Kind == ModerationActionKind.Ban || a.Kind == ModerationActionKind.Suspension)
                            && a.RevokedAt == null)
                .ToListAsync(ct);

            foreach (var previous in live)
            {
                previous.Revoke(actor.UserId, $"Lifted by {action.Reference}", now);
            }
        }

        Audit(actor, ModerationAuditActions.ActionIssued, id,
            $"{action.Kind} ({action.Reason}) ref {action.Reference}"
            + (expiresAt is null ? string.Empty : $" until {expiresAt:u}"));

        await Db.SaveChangesAsync(ct);

        if (request.Notify) mailer.QueueActionNotice(action, email, displayName);

        return Ok(ActionDto.From(action, now));
    }

    /// <summary>Promotes or demotes a staff account.</summary>
    [HttpPost("users/{id}/role")]
    public async Task<IActionResult> SetRoleAsync(
        string id, [FromBody] SetRoleRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        if (request.Role is not ("Default" or "Moderator" or "Admin"))
            return Failure(400, "invalid_role", "Role must be Default, Moderator or Admin.");

        if (id == actor.UserId)
            return Failure(400, "self_action", "You cannot change your own role. Ask another administrator.");

        SetUserRoleResponse result;
        try
        {
            result = await bus.InvokeAsync<SetUserRoleResponse>(new SetUserRoleRequest
            {
                UserId = id,
                ActorUserId = actor.UserId,
                Role = request.Role,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Identity did not answer the role change for {UserId}", id);
            return Failure(503, "identity_unavailable",
                "The account service did not answer. Nothing was changed - try again.");
        }

        if (!result.Success)
        {
            return Failure(
                result.FailureCode == "not_found" ? 404 : 409,
                result.FailureCode ?? "rejected",
                result.FailureMessage ?? "The account service refused the change.");
        }

        // Logged at Warning on both sides on purpose.
        Audit(actor, ModerationAuditActions.RoleChanged, id, $"-> {result.Role}");
        await Db.SaveChangesAsync(ct);

        logger.LogWarning("Administrator {ActorId} set the role of {UserId} to {Role}",
            actor.UserId, id, result.Role);

        return Ok(new { userId = id, role = result.Role, userName = result.UserName });
    }

    [HttpGet("users/{id}/actions")]
    public async Task<IActionResult> HistoryAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var now = DateTimeOffset.UtcNow;

        var actions = await Db.ModerationActions.AsNoTracking()
            .Where(a => a.TargetUserId == id)
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return Ok(actions.Select(a => ActionDto.From(a, now)));
    }

    /// <summary>Lifts a live sanction early.</summary>
    [HttpPost("actions/{id}/revoke")]
    public async Task<IActionResult> RevokeAsync(
        string id, [FromBody] RevokeActionRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var action = await Db.ModerationActions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (action is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        if (!action.Revoke(actor.UserId, request.Reason, now))
        {
            return Failure(409, "not_revocable",
                action.RevokedAt is not null
                    ? "That action has already been revoked."
                    : "Only a ban or a suspension can be revoked.");
        }

        string? email = null;
        string? displayName = null;

        // Only restore sign-in when nothing else is still holding the account down.
        var stillSanctioned = await Db.ModerationActions.AnyAsync(a =>
            a.TargetUserId == action.TargetUserId
            && a.Id != action.Id
            && (a.Kind == ModerationActionKind.Ban || a.Kind == ModerationActionKind.Suspension)
            && a.RevokedAt == null
            && (a.ExpiresAt == null || a.ExpiresAt > now), ct);

        if (!stillSanctioned)
        {
            try
            {
                var status = await bus.InvokeAsync<SetUserModerationStatusResponse>(
                    new SetUserModerationStatusRequest
                    {
                        UserId = action.TargetUserId,
                        ActorUserId = actor.UserId,
                        Banned = false,
                        Reason = $"Revoked {action.Reference}",
                    });

                if (!status.Success)
                {
                    return Failure(409, status.FailureCode ?? "rejected",
                        status.FailureMessage ?? "The account service refused to restore the account.");
                }

                email = status.Email;
                displayName = status.UserName;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Identity did not answer the restore for {UserId}", action.TargetUserId);
                return Failure(503, "identity_unavailable",
                    "The account service did not answer. Nothing was changed - try again.");
            }
        }

        Audit(actor, ModerationAuditActions.ActionRevoked, action.Id, request.Reason);

        await Db.SaveChangesAsync(ct);

        if (request.Notify && !stillSanctioned && email is not null)
        {
            // Reported to the user as the restoration it is, rather than as "your ban was revoked",
            // which reads like a status code.
            var notice = ModerationAction.Create(new CreateModerationActionParams
            {
                TargetUserId = action.TargetUserId,
                ActorUserId = actor.UserId,
                Kind = ModerationActionKind.Unban,
                Reason = action.Reason,
                PublicNote = request.Reason,
            }, now);

            mailer.QueueActionNotice(notice, email, displayName);
        }

        return Ok(ActionDto.From(action, now));
    }

    /// <summary>The target's staff tier, or None.</summary>
    private async Task<StaffRole> ResolveTargetRoleAsync(string userId)
    {
        try
        {
            var response = await bus.InvokeAsync<IsUserAdministrativeResponse>(
                new IsUserAdministrativeRequest { UserId = userId });

            return response.Role switch
            {
                "Admin" => StaffRole.Admin,
                "Moderator" => StaffRole.Moderator,
                _ => response.IsAdministrative ? StaffRole.Admin : StaffRole.None,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve the staff tier of {UserId}; treating them as staff.", userId);
            return StaffRole.Admin;
        }
    }
}
