using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Sites;
using Messaging;

namespace Echo.Moderation;

/// <summary>Sends the moderation and support mail, off the request path.</summary>
public class ModerationMailer(IServiceScopeFactory scopes, ILogger<ModerationMailer> logger)
{
    /// <summary>Where appeal and ticket links point.</summary>
    private static string SupportBaseUrl => SiteHost.BaseUrl(SiteHosting.SupportHost);

    /// <summary>Tells a user what was done to their account.</summary>
    public virtual void QueueActionNotice(ModerationAction action, string? email, string? displayName)
    {
        if (action.Kind == ModerationActionKind.Note) return;
        if (string.IsNullOrWhiteSpace(email)) return;

        var (subject, body) = ModerationEmails.ForAction(action, displayName, SupportBaseUrl);
        Queue(email, subject, body, $"action {action.Id}");
    }

    public virtual void QueueAppealDecision(ModerationAppeal appeal, ModerationAction action)
    {
        if (string.IsNullOrWhiteSpace(appeal.ContactEmail)) return;

        var (subject, body) = ModerationEmails.ForAppealDecision(appeal, action, SupportBaseUrl);
        Queue(appeal.ContactEmail, subject, body, $"appeal {appeal.Id}");
    }

    public virtual void QueueTicketOpened(SupportTicket ticket, string token)
    {
        var (subject, body) = ModerationEmails.ForTicketOpened(ticket, token, SupportBaseUrl);
        Queue(ticket.ContactEmail, subject, body, $"ticket {ticket.Id}");
    }

    /// <summary>Notifies the requester that staff replied.</summary>
    public virtual void QueueTicketReply(SupportTicket ticket, string replyBody, string? token)
    {
        var (subject, body) = ModerationEmails.ForTicketReply(
            ticket, replyBody, token ?? string.Empty, SupportBaseUrl);

        Queue(ticket.ContactEmail, subject, body, $"ticket {ticket.Id} reply");
    }

    private void Queue(string toAddress, string subject, string htmlBody, string what)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var mail = scope.ServiceProvider.GetRequiredService<EmailService>();

                // No-ops when the instance has no mail configured - see EmailService's constructor.
                await mail.SendEmailAsync(toAddress, subject, htmlBody);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send the notification for {What}", what);
            }
        });
    }
}
