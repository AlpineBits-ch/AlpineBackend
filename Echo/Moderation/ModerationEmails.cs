using System.Net;
using System.Text;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Sites;

namespace Echo.Moderation;

/// <summary>The HTML for every mail this feature sends.</summary>
public static class ModerationEmails
{
    private const string Bg = "#111318";
    private const string Card = "#1a1d26";
    private const string Border = "#2a2d3a";
    private const string Text = "#e2e4ea";
    private const string Muted = "#8b8fa8";
    private const string Faint = "#5a5e72";

    /// <summary>The subject and body for a moderation action.</summary>
    public static (string Subject, string Body) ForAction(
        ModerationAction action, string? displayName, string supportBaseUrl)
    {
        var (headline, badge, accent) = action.Kind switch
        {
            ModerationActionKind.Warning =>
                ("Your account has received a warning", "Warning", "#d9a441"),
            ModerationActionKind.Suspension =>
                ("Your account has been temporarily suspended", "Suspension", "#e0955c"),
            ModerationActionKind.Ban =>
                ("Your account has been banned", "Account banned", "#e06c75"),
            ModerationActionKind.Unban =>
                ("Your account has been restored", "Account restored", "#46b98a"),
            _ => ("An update about your account", "Account update", "#56a8f5"),
        };

        var body = new StringBuilder();

        body.Append(Paragraph(
            action.Kind == ModerationActionKind.Unban
                ? "A moderator has reviewed your account and lifted the restriction on it. You can sign in again now."
                : $"A moderator reviewed your account and took the action below. This was a decision made by a person, not automatically."));

        body.Append(Divider());

        body.Append(InfoRow("Reason", ReasonText(action.Reason)));

        if (action.Kind == ModerationActionKind.Suspension && action.ExpiresAt is not null)
        {
            body.Append(InfoRow("Ends", action.ExpiresAt.Value.UtcDateTime.ToString("d MMMM yyyy, HH:mm 'UTC'")));
        }
        else if (action.Kind == ModerationActionKind.Ban)
        {
            body.Append(InfoRow("Duration", action.ExpiresAt is null
                ? "Indefinite"
                : $"Until {action.ExpiresAt.Value.UtcDateTime:d MMMM yyyy, HH:mm 'UTC'}"));
        }

        body.Append(InfoRow("Reference", action.Reference, mono: true));

        if (!string.IsNullOrWhiteSpace(action.PublicNote))
        {
            body.Append(Divider());
            body.Append($"""
                <div style="font-size:11px;color:{Faint};text-transform:uppercase;letter-spacing:.6px;font-weight:500;margin-bottom:10px;">What the moderator wrote</div>
                <div style="background:{Bg};border:1px solid {Border};border-radius:8px;padding:16px 18px;font-size:14px;line-height:1.6;color:{Text};margin-bottom:24px;">{Multiline(action.PublicNote)}</div>
                """);
        }

        // No appeal block on an unban or a warning.
        if (action.Kind is ModerationActionKind.Ban or ModerationActionKind.Suspension)
        {
            var appealUrl = $"{supportBaseUrl}/appeal?ref={WebUtility.UrlEncode(action.Reference)}";

            body.Append(Divider());
            body.Append($"""
                <div style="font-size:15px;font-weight:600;color:#ffffff;margin-bottom:8px;">If you think this is wrong</div>
                <p style="font-size:14px;color:{Muted};line-height:1.6;margin:0 0 20px;">
                  You can appeal this once. Tell us what you think we got wrong and a person will read it.
                  Quote the reference above.
                </p>
                <a href="{Escape(appealUrl)}" style="display:inline-block;background:{accent};color:#0d1117;font-size:14px;font-weight:600;text-decoration:none;padding:11px 22px;border-radius:8px;">Appeal this decision</a>
                <p style="font-size:12px;color:{Faint};line-height:1.6;margin:16px 0 0;">
                  Or go to <a href="{Escape(supportBaseUrl)}" style="color:{Muted};">{Escape(StripScheme(supportBaseUrl))}</a> and enter the reference by hand.
                </p>
                """);
        }

        return (headline, Wrap(headline, badge, accent, displayName, body.ToString()));
    }

    /// <summary>The outcome of an appeal.</summary>
    public static (string Subject, string Body) ForAppealDecision(
        ModerationAppeal appeal, ModerationAction action, string supportBaseUrl)
    {
        var granted = appeal.Status == AppealStatus.Granted;

        var headline = granted ? "Your appeal was accepted" : "Your appeal was not accepted";
        var accent = granted ? "#46b98a" : "#e06c75";

        var body = new StringBuilder();

        body.Append(Paragraph(granted
            // Careful wording: granting an appeal records the decision, a moderator then issues the
            // unban. Promising immediate access here would be a promise the system does not keep.
            ? "A moderator reviewed your appeal and agreed with it. The restriction on your account is being lifted - you will get a second email once that has gone through, and you will be able to sign in then."
            : "A moderator reviewed your appeal and decided the original action stands."));

        body.Append(Divider());
        body.Append(InfoRow("Original action", $"{ActionText(action.Kind)} · {ReasonText(action.Reason)}"));
        body.Append(InfoRow("Reference", action.Reference, mono: true));

        body.Append(Divider());
        body.Append($"""
            <div style="font-size:11px;color:{Faint};text-transform:uppercase;letter-spacing:.6px;font-weight:500;margin-bottom:10px;">The decision</div>
            <div style="background:{Bg};border:1px solid {Border};border-radius:8px;padding:16px 18px;font-size:14px;line-height:1.6;color:{Text};margin-bottom:8px;">{Multiline(appeal.DecisionNote ?? string.Empty)}</div>
            """);

        if (!granted)
        {
            // Said plainly, and said here rather than left to be inferred.
            var permanent = action.ExpiresAt is null;

            body.Append($"""
                <div style="height:1px;background:{Border};margin:22px 0;"></div>
                <div style="font-size:15px;font-weight:600;color:#ffffff;margin-bottom:8px;">This decision is final</div>
                <p style="font-size:14px;color:{Muted};line-height:1.6;margin:0 0 14px;">
                  Each moderation decision can be appealed once, and this was that appeal. There is no
                  further appeal, and submitting another one will not reach a different person.
                  {(permanent
                      ? "The restriction on your account has no end date."
                      : $"The restriction still ends on {action.ExpiresAt!.Value.UtcDateTime:d MMMM yyyy}, as originally stated.")}
                </p>
                <p style="font-size:12px;color:{Faint};line-height:1.6;margin:0;">
                  A moderator can still lift a restriction later if genuinely new information comes to
                  light. That is at their discretion, not something you can request again - please do
                  not treat it as a second appeal.
                </p>
                """);
        }

        return (headline, Wrap(headline, granted ? "Appeal accepted" : "Appeal declined", accent, null, body.ToString()));
    }

    /// <summary>Confirms a ticket was received and hands over the link that opens it.</summary>
    public static (string Subject, string Body) ForTicketOpened(
        SupportTicket ticket, string token, string supportBaseUrl)
    {
        var url = TicketUrl(ticket, token, supportBaseUrl);

        var body = new StringBuilder();

        body.Append(Paragraph(
            "We have your message. A person will read it and reply by email - most tickets are answered within a couple of days."));

        body.Append(Divider());
        body.Append(InfoRow("Subject", ticket.Subject));
        body.Append(InfoRow("Category", ticket.Category.ToString()));
        body.Append(InfoRow("Reference", ticket.Reference, mono: true));

        body.Append(Divider());
        body.Append($"""
            <a href="{Escape(url)}" style="display:inline-block;background:#4B5BC4;color:#ffffff;font-size:14px;font-weight:600;text-decoration:none;padding:11px 22px;border-radius:8px;">Open your ticket</a>
            <p style="font-size:12px;color:{Faint};line-height:1.6;margin:16px 0 0;">
              Keep this email - the link above is the only way back into this ticket, and we cannot
              send you another one. Anyone with the link can read and reply to it.
            </p>
            """);

        return ($"[{ticket.Reference}] {ticket.Subject}", Wrap("We got your message", "Support", "#4B5BC4", null, body.ToString()));
    }

    /// <summary>A staff reply landed.</summary>
    public static (string Subject, string Body) ForTicketReply(
        SupportTicket ticket, string replyBody, string token, string supportBaseUrl)
    {
        var url = TicketUrl(ticket, token, supportBaseUrl);

        var body = new StringBuilder();

        body.Append(Paragraph("Support replied to your ticket."));
        body.Append(Divider());
        body.Append($"""
            <div style="background:{Bg};border:1px solid {Border};border-radius:8px;padding:16px 18px;font-size:14px;line-height:1.6;color:{Text};margin-bottom:24px;">{Multiline(replyBody)}</div>
            <a href="{Escape(url)}" style="display:inline-block;background:#4B5BC4;color:#ffffff;font-size:14px;font-weight:600;text-decoration:none;padding:11px 22px;border-radius:8px;">Reply</a>
            """);

        return ($"Re: [{ticket.Reference}] {ticket.Subject}",
            Wrap("Support replied", "Support", "#4B5BC4", null, body.ToString()));
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

    // ── Building blocks ─────────────────────────────────────────────────────

    private static string Wrap(string title, string badge, string accent, string? name, string content) =>
        $"""
        <!DOCTYPE html>
        <html lang="en"><head>
        <meta charset="UTF-8" /><meta name="viewport" content="width=device-width,initial-scale=1" />
        <title>{Escape(title)}</title></head>
        <body style="margin:0;background:{Bg};color:{Text};font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Inter,Helvetica,Arial,sans-serif;-webkit-font-smoothing:antialiased;">
          <div style="max-width:560px;margin:48px auto;padding:0 16px 48px;">
            <div style="padding:32px 0 24px;">
              <span style="display:inline-block;width:28px;height:28px;border-radius:8px;background:#4B5BC4;vertical-align:middle;"></span>
              <span style="font-size:15px;font-weight:600;color:#ffffff;letter-spacing:-.2px;margin-left:10px;vertical-align:middle;">venta</span>
            </div>
            <div style="background:{Card};border:1px solid {Border};border-radius:12px;overflow:hidden;">
              <div style="height:3px;background:{accent};"></div>
              <div style="padding:36px 36px 32px;">
                <div style="display:inline-block;background:rgba(255,255,255,.06);color:{accent};font-size:11px;font-weight:600;letter-spacing:.8px;text-transform:uppercase;padding:4px 10px;border-radius:20px;border:1px solid {Border};margin-bottom:20px;">{Escape(badge)}</div>
                <h1 style="font-size:23px;font-weight:600;color:#ffffff;letter-spacing:-.4px;line-height:1.3;margin:0 0 14px;">{Escape(title)}{(name is null ? string.Empty : $", {Escape(name)}")}</h1>
                {content}
              </div>
            </div>
            <div style="padding:24px 0 0;text-align:center;font-size:12px;color:#3a3d50;line-height:1.7;">
              This is an automated message. Replies to this address are not read.
            </div>
          </div>
        </body></html>
        """;

    private static string Paragraph(string text) =>
        $"""<p style="font-size:15px;color:{Muted};line-height:1.6;margin:0 0 26px;">{Escape(text)}</p>""";

    private static string Divider() =>
        $"""<div style="height:1px;background:{Border};margin:0 0 24px;"></div>""";

    private static string InfoRow(string label, string value, bool mono = false)
    {
        var font = mono
            ? "ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;letter-spacing:1px"
            : "inherit";

        return $"""
            <div style="margin-bottom:18px;">
              <div style="font-size:11px;color:{Faint};text-transform:uppercase;letter-spacing:.6px;font-weight:500;margin-bottom:3px;">{Escape(label)}</div>
              <div style="font-size:14px;color:{Text};font-family:{font};">{Escape(value)}</div>
            </div>
            """;
    }

    /// <summary>Escapes, then turns newlines into breaks.</summary>
    private static string Multiline(string text) =>
        Escape(text).Replace("\r\n", "\n").Replace("\n", "<br />");

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string StripScheme(string url) =>
        url.Replace("https://", string.Empty).Replace("http://", string.Empty);
}
