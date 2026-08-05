using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>Promotes and demotes staff.</summary>
public class SetUserRoleHandler
{
    public async Task<SetUserRoleResponse> Handle(
        SetUserRoleRequest request,
        MicroserviceContext ctx,
        ILogger<SetUserRoleHandler> logger)
    {
        if (!Enum.TryParse<UserType>(request.Role, ignoreCase: true, out var target)
            || target == UserType.Bot)
        {
            // Bot is refused rather than accepted: bot-ness is a property of how the account was
            // created (CreateBot skips email, age verification and the welcome path entirely), not
            // a role to be assigned afterwards.
            return Refuse("invalid_role", "Role must be Default, Moderator or Admin.");
        }

        // Checked before the row is loaded.
        if (request.UserId == request.ActorUserId)
            return Refuse("self_action", "You cannot change your own role. Ask another administrator.");

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
        if (user is null) return Refuse("not_found", "No such account.");

        if (user.UserType == UserType.Bot)
            return Refuse("bot_account", "Bot accounts cannot be given a staff role.", user);

        var current = user.UserType;

        if (current == target)
        {
            // Not an error - two administrators setting the same tier is a race, and the second
            // should see the state the first produced.
            return new SetUserRoleResponse
            {
                Success = true,
                FailureCode = "no_change",
                Role = current.ToString(),
                UserName = user.UserName,
            };
        }

        // Promoting a banned or half-deleted account would hand the console to somebody who cannot
        // sign in, and un-ban them by the back door the moment anyone noticed and "fixed" it.
        if (target != UserType.Default && user.Status != UserStatus.Active)
            return Refuse("account_inactive", $"The account is {user.Status} and cannot be given a staff role.", user);

        // The last-administrator guard.
        if (current == UserType.Admin && target != UserType.Admin)
        {
            var remaining = await ctx.Users.CountAsync(u =>
                u.UserType == UserType.Admin
                && u.Status == UserStatus.Active
                && u.Id != user.Id);

            if (remaining == 0)
            {
                logger.LogWarning(
                    "Refused to demote {UserId}: they are the last active administrator on this instance.",
                    user.Id);

                return Refuse("last_administrator",
                    "This is the only active administrator on the instance. Promote someone else first.",
                    user);
            }
        }

        user.UserType = target;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = user.Id,
            Action = IdentityAuditActions.StaffRoleChanged,
            Detail = $"{current} -> {target} by {request.ActorUserId}",
        }));

        // No SaveChangesAsync: Wolverine's AutoApplyTransactions middleware commits the injected
        // DbContext when the handler returns, and calling it here would put the audit row outside
        // the transaction the middleware manages.

        logger.LogWarning(
            "Staff role change: {UserId} moved {From} -> {To} by administrator {ActorId}",
            user.Id, current, target, request.ActorUserId);

        return new SetUserRoleResponse
        {
            Success = true,
            Role = target.ToString(),
            UserName = user.UserName,
        };
    }

    private static SetUserRoleResponse Refuse(string code, string message, ApplicationUser? user = null) =>
        new()
        {
            Success = false,
            FailureCode = code,
            FailureMessage = message,
            Role = user?.UserType.ToString() ?? string.Empty,
            UserName = user?.UserName,
        };
}
