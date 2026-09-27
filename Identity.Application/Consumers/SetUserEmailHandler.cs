using FluentValidation;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>Replaces an account's sign-in address for the moderation console.</summary>
public class SetUserEmailHandler
{
    /// <summary>The column length ASP.NET Identity maps for Email and NormalizedEmail.</summary>
    private const int MaxEmailLength = 256;

    public async Task<SetUserEmailResponse> Handle(
        SetUserEmailRequest request,
        MicroserviceContext ctx,
        ILogger<SetUserEmailHandler> logger)
    {
        if (request.UserId == request.ActorUserId)
            return Refuse("self_action", "You cannot change your own address here. Ask another administrator.");

        var address = request.Email?.Trim() ?? string.Empty;
        if (address.Length > MaxEmailLength)
            return Refuse("invalid_email", $"An address cannot be longer than {MaxEmailLength} characters.");

        Email email;
        try
        {
            email = new Email(address);
        }
        catch (ValidationException ex)
        {
            return Refuse("invalid_email", ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Invalid email format");
        }

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
        if (user is null) return Refuse("not_found", "No such account.");

        if (user.UserType == UserType.Bot)
            return Refuse("bot_account", "Bot accounts have no sign-in address.", user);

        if (user.Status is UserStatus.PendingDeletion or UserStatus.PurgeInProgress or UserStatus.Deleted)
            return Refuse("invalid_state", $"The account is {user.Status} and cannot be changed here.", user);

        if (user.Email == email.Value)
        {
            return new SetUserEmailResponse
            {
                Success = true,
                FailureCode = "no_change",
                Email = user.Email,
                PreviousEmail = user.Email,
                UserName = user.UserName,
            };
        }

        var normalized = email.Value.ToUpperInvariant();
        var taken = await ctx.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != user.Id);
        if (taken) return Refuse("email_taken", "Another account already uses that address.", user);

        var previous = user.Email;
        var now = DateTimeOffset.UtcNow;

        user.SetEmailByStaff(email, now);

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = user.Id,
            Action = IdentityAuditActions.EmailChangedByStaff,
            Detail = $"{previous ?? "(none)"} -> {email.Value} by {request.ActorUserId}",
        }));

        // The address is a sign-in credential, so this ends sessions the way a password reset does.
        var sessions = await ctx.LoginSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ToListAsync();

        foreach (var session in sessions) session.Revoke();

        if (sessions.Count > 0)
        {
            ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
            {
                UserId = user.Id,
                Action = IdentityAuditActions.SessionsRevoked,
                Detail = $"staff email change revoked {sessions.Count} active session(s)",
            }));
        }

        logger.LogWarning(
            "Sign-in address of {UserId} replaced by administrator {ActorId}; {Sessions} session(s) revoked",
            user.Id, request.ActorUserId, sessions.Count);

        return new SetUserEmailResponse
        {
            Success = true,
            Email = user.Email,
            PreviousEmail = previous,
            UserName = user.UserName,
            SessionsRevoked = sessions.Count,
        };
    }

    private static SetUserEmailResponse Refuse(string code, string message, ApplicationUser? user = null) =>
        new()
        {
            Success = false,
            FailureCode = code,
            FailureMessage = message,
            Email = user?.Email,
            PreviousEmail = user?.Email,
            UserName = user?.UserName,
        };
}
