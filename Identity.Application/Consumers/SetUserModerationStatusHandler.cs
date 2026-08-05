using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>Makes a ban actually take effect.</summary>
public class SetUserModerationStatusHandler
{
    public async Task<SetUserModerationStatusResponse> Handle(
        SetUserModerationStatusRequest request,
        MicroserviceContext ctx,
        ILogger<SetUserModerationStatusHandler> logger)
    {
        // Self-action is checked before the row is even loaded: a staff member banning themselves is
        // never intentional, and it locks the console's own operator out of the console.
        if (request.UserId == request.ActorUserId)
        {
            return Refuse("self_action", "You cannot change your own account's status.");
        }

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
        if (user is null) return Refuse("not_found", "No such account.");

        var current = user.Status;

        // Admins are not bannable through this path.
        if (user.UserType == UserType.Admin)
        {
            logger.LogWarning(
                "Refused moderation status change on administrator {UserId} by {ActorId}",
                user.Id, request.ActorUserId);

            return Refuse("protected_account", "Administrator accounts cannot be banned.", current, user);
        }

        var target = request.Banned ? UserStatus.Banned : UserStatus.Active;

        if (current == target)
        {
            // Not an error.
            return new SetUserModerationStatusResponse
            {
                Success = true,
                FailureCode = "no_change",
                Status = current.ToString(),
                Email = user.Email,
                UserName = user.UserName,
            };
        }

        // An account midway through erasure is not a candidate for either verb.
        if (current is UserStatus.PendingDeletion or UserStatus.PurgeInProgress or UserStatus.Deleted)
        {
            return Refuse("invalid_state", $"The account is {current} and cannot be changed here.", current, user);
        }

        user.Status = target;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Written against the banned account rather than the staff member, so it lands on that
        // user's own security timeline beside their logins.
        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = user.Id,
            Action = IdentityAuditActions.ModerationStatusChanged,
            Detail = $"{current} -> {target} by {request.ActorUserId}"
                     + (string.IsNullOrWhiteSpace(request.Reason) ? string.Empty : $": {request.Reason}"),
        }));

        // No SaveChangesAsync: Wolverine's AutoApplyTransactions middleware commits the injected
        // DbContext when the handler returns.

        logger.LogInformation(
            "Account {UserId} moved {From} -> {To} by staff {ActorId}",
            user.Id, current, target, request.ActorUserId);

        return new SetUserModerationStatusResponse
        {
            Success = true,
            Status = target.ToString(),
            Email = user.Email,
            UserName = user.UserName,
        };
    }

    private static SetUserModerationStatusResponse Refuse(
        string code, string message, UserStatus? status = null, ApplicationUser? user = null) =>
        new()
        {
            Success = false,
            FailureCode = code,
            FailureMessage = message,
            Status = status?.ToString() ?? string.Empty,
            Email = user?.Email,
            UserName = user?.UserName,
        };
}
