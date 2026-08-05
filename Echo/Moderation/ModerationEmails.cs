using System.Net;
using System.Text;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Messaging;
using static Messaging.EmailLayout;

namespace Echo.Moderation;

/// <summary>The content of every mail this feature sends.</summary>
public static class ModerationEmails
{
    /// <summary>The subject and body for a moderation action.</summary>
    public static (string Subject, string Body) ForAction(
        ModerationAction action, string? displayName, string supportBaseUrl)
    {
        var (headline, badge, accent) = action.Kind switch
        {
            ModerationActionKind.Warning =>
                ("Your account has received a warning", "Warning", Warning),
            ModerationActionKind.Suspension =>
                ("Your account has been temporarily suspended", "Suspension", Warning),
            ModerationActionKind.Ban =>
                ("Your account has been banned", "Account banned", Danger),
            ModerationActionKind.Unban =>
                ("Your account has been restored", "Account restored", Success),
            _ => ("An update about your account", "Account update", Info),
        };

        var body = new StringBuilder();

        body.Append(Paragraph(action.Kind == ModerationActionKind.Unban
            ? "A moderator reviewed your account and lifted the restriction on it. You can sign in again now."
            : "A moderator reviewed your account and took the action below."));

        body.Append(Divider());

        body.Append(InfoRow("Reason", ReasonText(action.Reason)));

        if (action.Kind == ModerationActionKind.Suspension && action.ExpiresAt is not null)
        {
            body.Append(InfoRow("Ends", $"{action.ExpiresAt.Value.UtcDateTime:d MMMM yyyy, HH:mm} UTC"));
        }
        else if (action.Kind == ModerationActionKind.Ban)
        {
            body.Append(InfoRow("Duration", action.ExpiresAt is null
                ? "Indefinite"
                : $"Until {action.ExpiresAt.Value.UtcDateTime:d MMMM yyyy, HH:mm} UTC"));
        }

        body.Append(InfoRow("Reference", action.Reference, mono: true));

        if (!string.IsNullOrWhiteSpace(action.PublicNote))
        {
            body.Append(Divider());
            body.Append(Heading("What the moderator wrote"));
            body.Append(Quote(action.PublicNote));
        }

        // No appeal block on an unban or a warning.
        if (action.Kind is ModerationActionKind.Ban or ModerationActionKind.Suspension)
        {
            var appealUrl = $"{supportBaseUrl}/appeal?ref={WebUtility.UrlEncode(action.Reference)}";

            body.Append(Divider());
            body.Append(Heading("If you think this is wrong"));
            body.Append(Paragraph(
                "You can appeal this once. Quote the reference above and tell us what you think we got wrong."));
            body.Append(Button("Appeal this decision", appealUrl));
            body.Append(RawNote(
                $"Or go to {Link(StripScheme(supportBaseUrl), supportBaseUrl)} and enter the reference by hand."));
        }

        return (headline, Page(
            headline, badge, accent, body.ToString(),
            preheader: PreheaderFor(action),
            footer: displayName is null
                ? null
                : $"Sent to the address on the account for {displayName}. This is an automated message."));
    }

    /// <summary>What the inbox shows beside the subject.</summary>
    private static string PreheaderFor(ModerationAction action) => action.Kind switch
    {
        ModerationActionKind.Unban => "The restriction on your account has been lifted.",
        ModerationActionKind.Warning => $"{ReasonText(action.Reason)}. No restriction on your account.",
        _ => $"{ReasonText(action.Reason)}. Reference {action.Reference} - you can appeal this once.",
    };

    /// <summary>The outcome of an appeal.</summary>
    public static (string Subject, string Body) ForAppealDecision(
        ModerationAppeal appeal, ModerationAction action, string supportBaseUrl)
    {
        var granted = appeal.Status == AppealStatus.Granted;

        var headline = granted ? "Your appeal was accepted" : "Your appeal was not accepted";
        var accent = granted ? Success : Danger;

        var body = new StringBuilder();

        body.Append(Paragraph(granted
            // Careful wording: granting an appeal records the decision, a moderator then issues the
            // unban. Promising immediate access here would be a promise the system does not keep.
            ? "A moderator reviewed your appeal and agreed with it. The restriction on your account "
              + "is being lifted - you will get a second email once that has gone through, and you "
              + "will be able to sign in then."
            : "A moderator reviewed your appeal and decided the original action stands."));

        body.Append(Divider());
        body.Append(InfoRow("Original action", $"{ActionText(action.Kind)} - {ReasonText(action.Reason)}"));
        body.Append(InfoRow("Reference", action.Reference, mono: true));

        body.Append(Divider());
        body.Append(Heading("The decision"));
        body.Append(Quote(appeal.DecisionNote ?? string.Empty));

        if (!granted)
        {
            // Said plainly, and said here rather than left to be inferred.
            body.Append(Divider());
            body.Append(Heading("This decision is final"));
            body.Append(Paragraph(
                "Each moderation decision can be appealed once, and this was that appeal. There is "
                + "no further appeal, and submitting another one will not get it looked at again. "
                + (action.ExpiresAt is null
                    ? "The restriction on your account has no end date."
                    : $"The restriction still ends on {action.ExpiresAt.Value.UtcDateTime:d MMMM yyyy}, as originally stated.")));

            body.Append(Note(
                "A moderator can still lift a restriction later if genuinely new information comes "
                + "to light. That is at their discretion, not something you can request again - "
                + "please do not treat it as a second appeal."));
        }

        return (headline, Page(
            headline, granted ? "Appeal accepted" : "Appeal declined", accent, body.ToString(),
            preheader: granted
                ? "The restriction is being lifted; a second email follows when it has gone through."
                : $"The original action stands. Reference {action.Reference}."));
    }

    /// <summary>Confirms a ticket was received and hands over the link that opens it.</summary>
    public static (string Subject, string Body) ForTicketOpened(
        SupportTicket ticket, string token, string supportBaseUrl)
    {
        var body = Compose(
            Paragraph("We have your message. We'll reply to this address, usually within a couple of days."),
            Divider(),
            InfoRow("Subject", ticket.Subject),
            InfoRow("Category", ticket.Category.ToString()),
            InfoRow("Reference", ticket.Reference, mono: true),
            Divider(),
            Button("Open your ticket", TicketUrl(ticket, token, supportBaseUrl)),
            Note("Keep this email - the link above is the only way back into your ticket, and we "
                 + "cannot send you another one. Anyone with the link can read and reply to it."));

        return ($"[{ticket.Reference}] {ticket.Subject}",
            Page("We got your message", "Support", Brand, body,
                preheader: $"Reference {ticket.Reference}. Keep this email - the link is the only way back in."));
    }

    /// <summary>A staff reply landed.</summary>
    public static (string Subject, string Body) ForTicketReply(
        SupportTicket ticket, string replyBody, string? token, string supportBaseUrl)
    {
        var body = new StringBuilder();

        body.Append(Paragraph("Support replied to your ticket."));
        body.Append(Divider());
        body.Append(Quote(replyBody));

        // Only when the token is in hand.
        if (!string.IsNullOrEmpty(token))
        {
            body.Append(Button("Reply", TicketUrl(ticket, token, supportBaseUrl)));
        }
        else
        {
            body.Append(RawNote(
                $"Reply from the link in your original {Escape(ticket.Reference)} email, or at "
                + $"{Link(StripScheme(supportBaseUrl), supportBaseUrl)}."));
        }

        return ($"Re: [{ticket.Reference}] {ticket.Subject}",
            Page("Support replied", "Support", Brand, body.ToString(), preheader: Preview(replyBody)));
    }

    /// <summary>The opening of a reply, for the inbox preview.</summary>
    private static string Preview(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 140 ? flat : flat[..140] + "...";
    }

    private static string TicketUrl(SupportTicket ticket, string token, string supportBaseUrl) =>
        $"{supportBaseUrl}/ticket?ref={WebUtility.UrlEncode(ticket.Reference)}&token={WebUtility.UrlEncode(token)}";

    public static string ActionText(ModerationActionKind kind) => kind switch
    {
        ModerationActionKind.Warning => "Warning",
        ModerationActionKind.Suspension => "Temporary suspension",
        ModerationActionKind.Ban => "Account ban",
        ModerationActionKind.Unban => "Restriction lifted",
        _ => "Note",
    };

    /// <summary>The reason in words a person can act on.</summary>
    public static string ReasonText(ReportReason reason) => reason switch
    {
        ReportReason.Spam => "Spam or unsolicited advertising",
        ReportReason.Harassment => "Harassment or targeted abuse",
        ReportReason.HateSpeech => "Hateful conduct",
        ReportReason.ViolentThreats => "Threats of violence",
        ReportReason.SelfHarm => "Content promoting self-harm",
        ReportReason.SexualContent => "Unwanted sexual content",
        ReportReason.ChildSafety => "Content endangering a minor",
        ReportReason.Impersonation => "Impersonation",
        ReportReason.Malware => "Malware or malicious links",
        ReportReason.IllegalContent => "Illegal content",
        _ => "Breach of the community rules",
    };

    private static string StripScheme(string url) =>
        url.Replace("https://", string.Empty).Replace("http://", string.Empty);
}
