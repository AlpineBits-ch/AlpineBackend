using System.Net;
using System.Text;

namespace Messaging;

/// <summary>
/// The shared shell for every transactional mail this platform sends.
///
/// <para><b>Why this exists: the old mails rendered as near-invisible text.</b> They were dark-themed
/// (<c>#111318</c> background, <c>#e2e4ea</c> text) with all of the styling in a single
/// <c>&lt;head&gt;&lt;style&gt;</c> block hung off the <c>body</c> selector. Two independent things
/// then went wrong, and both are normal email-client behaviour rather than bugs:</para>
///
/// <list type="number">
///   <item>Gmail rewrites <c>&lt;body&gt;</c> into a <c>&lt;div&gt;</c> and drops rules attached to
///   it; Outlook's desktop client renders through Word and ignores much of a stylesheet. The dark
///   <em>background</em> disappeared while the light <em>text colours</em> survived, leaving
///   near-white text on white.</item>
///   <item>Clients that force dark mode invert an already-dark design, which turns considered
///   contrast into mush.</item>
/// </list>
///
/// <para><b>So the rules here are not stylistic preferences.</b> Light theme, so that stripping
/// every style still leaves dark text on white and a readable mail. Tables with <c>bgcolor</c>
/// attributes rather than styled divs, because that is what Word's renderer honours. Every
/// declaration inline, because a stripped <c>&lt;style&gt;</c> block must not be able to take the
/// design with it. No gradients and no flexbox - Outlook renders the first as nothing and collapses
/// the second. And <c>color-scheme: light</c> declared both ways, which is what stops the
/// better-behaved clients auto-inverting.</para>
///
/// <para>Every text colour here clears 4.5:1 on white. That is the floor, not the target: these are
/// read on phones, in sunlight, by people who have just been told their account is gone.</para>
/// </summary>
public static class EmailLayout
{
    public const string PageBackground = "#f4f6f8";
    public const string CardBackground = "#ffffff";
    public const string Border = "#e3e7ec";
    public const string Rule = "#eef1f5";

    /// <summary>#14171d on white: 15.8:1.</summary>
    public const string Text = "#14171d";

    /// <summary>#5b6472 on white: 6.4:1.</summary>
    public const string Muted = "#5b6472";

    /// <summary>#646d79 on white: 4.9:1. The floor - nothing quieter than this carries meaning.</summary>
    public const string Faint = "#646d79";

    /// <summary>Alpine's brand indigo.</summary>
    public const string Brand = "#4B5BC4";

    public const string Danger = "#b3261e";
    public const string Warning = "#8a6100";
    public const string Success = "#197a52";
    public const string Info = "#1d5fa8";

    private const string FontStack =
        "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    private const string MonoStack =
        "ui-monospace,SFMono-Regular,Menlo,Consolas,'Courier New',monospace";

    /// <summary>Wraps rendered body content in the page shell.</summary>
    /// <param name="title">Subject-shaped heading, shown at the top of the card.</param>
    /// <param name="badge">Small label above the heading.</param>
    /// <param name="accent">Colour of the badge and the rule under the card's top edge.</param>
    /// <param name="body">Already-escaped HTML, built from the helpers below.</param>
    /// <param name="footer">Small print under the card.</param>
    /// <param name="preheader">The line an inbox shows beside the subject.</param>
    public static string Page(
        string title, string? badge, string accent, string body, string? footer = null,
        string? preheader = null)
    {
        var brand = $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0">
              <tr>
                <td style="padding-right:10px;">
                  <div style="width:26px;height:26px;border-radius:7px;background:{Brand};"></div>
                </td>
                <td style="font-family:{FontStack};font-size:15px;font-weight:600;color:{Text};letter-spacing:-.01em;">venta</td>
              </tr>
            </table>
            """;

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width,initial-scale=1" />
            <meta name="color-scheme" content="light" />
            <meta name="supported-color-schemes" content="light" />
            <title>{Escape(title)}</title>
            </head>
            <body style="margin:0;padding:0;background-color:{PageBackground};">
            {Preheader(preheader)}
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{PageBackground}" style="background-color:{PageBackground};">
              <tr>
                <td align="center" style="padding:32px 12px 48px;">

                  <table role="presentation" width="560" cellpadding="0" cellspacing="0" border="0" style="width:100%;max-width:560px;">
                    <tr>
                      <td style="padding:0 4px 18px;">{brand}</td>
                    </tr>

                    <tr>
                      <td bgcolor="{CardBackground}" style="background-color:{CardBackground};border:1px solid {Border};border-radius:12px;">
                        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                          <tr>
                            <td bgcolor="{accent}" style="background-color:{accent};height:3px;line-height:3px;font-size:0;border-radius:12px 12px 0 0;">&nbsp;</td>
                          </tr>
                          <tr>
                            <td style="padding:30px 32px 30px;font-family:{FontStack};">
                              {BadgeMarkup(badge, accent)}
                              <h1 style="margin:0 0 14px;font-family:{FontStack};font-size:22px;line-height:1.3;font-weight:600;color:{Text};letter-spacing:-.02em;">{Escape(title)}</h1>
                              {body}
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>

                    <tr>
                      <td style="padding:20px 8px 0;font-family:{FontStack};font-size:12px;line-height:1.6;color:{Faint};">
                        {Escape(footer ?? "This is an automated message. Replies to this address are not read.")}
                      </td>
                    </tr>
                  </table>

                </td>
              </tr>
            </table>
            </body>
            </html>
            """;
    }

    /// <summary>The inbox preview line, hidden in the body.</summary>
    private static string Preheader(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"""
               <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:{PageBackground};">
                 {Escape(text)}{string.Concat(Enumerable.Repeat("&#8199;&#65279;&#847; ", 30))}
               </div>
               """;

    private static string BadgeMarkup(string? badge, string accent) =>
        string.IsNullOrWhiteSpace(badge)
            ? string.Empty
            : $"""
               <div style="font-family:{FontStack};font-size:11px;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:{accent};margin:0 0 12px;">{Escape(badge)}</div>
               """;

    // ── Body blocks ─────────────────────────────────────────────────────────

    public static string Paragraph(string text, string? color = null) =>
        $"""<p style="margin:0 0 20px;font-family:{FontStack};font-size:15px;line-height:1.6;color:{color ?? Muted};">{Escape(text)}</p>""";

    /// <summary>A paragraph that may carry pre-built inline markup - a link, a bolded clause.
    /// Callers are responsible for escaping anything interpolated into it.</summary>
    public static string RawParagraph(string html, string? color = null) =>
        $"""<p style="margin:0 0 20px;font-family:{FontStack};font-size:15px;line-height:1.6;color:{color ?? Muted};">{html}</p>""";

    public static string Divider() =>
        $"""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr><td bgcolor="{Rule}" style="background-color:{Rule};height:1px;line-height:1px;font-size:0;">&nbsp;</td></tr></table><div style="height:22px;line-height:22px;font-size:0;">&nbsp;</div>""";

    public static string Heading(string text) =>
        $"""<div style="font-family:{FontStack};font-size:15px;font-weight:600;color:{Text};margin:0 0 8px;">{Escape(text)}</div>""";

    /// <summary>A labelled fact.</summary>
    public static string InfoRow(string label, string value, bool mono = false) =>
        $"""
         <div style="margin:0 0 16px;">
           <div style="font-family:{FontStack};font-size:11px;font-weight:600;letter-spacing:.07em;text-transform:uppercase;color:{Faint};margin:0 0 3px;">{Escape(label)}</div>
           <div style="font-family:{(mono ? MonoStack : FontStack)};font-size:14px;line-height:1.5;color:{Text};{(mono ? "letter-spacing:.06em;font-weight:600;" : string.Empty)}">{Escape(value)}</div>
         </div>
         """;

    /// <summary>A one-time code, sized to be read off a screen and typed into another device.</summary>
    public static string CodeBlock(string code) =>
        $"""
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 14px;">
           <tr>
             <td align="center" bgcolor="#f7f9fb" style="background-color:#f7f9fb;border:1px solid {Border};border-radius:8px;padding:18px 12px;font-family:{MonoStack};font-size:30px;font-weight:700;letter-spacing:.28em;color:{Text};text-indent:.28em;">{Escape(code)}</td>
           </tr>
         </table>
         """;

    /// <summary>Quoted text from a person - a moderator's note, a support reply.</summary>
    public static string Quote(string text) =>
        $"""
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 22px;">
           <tr>
             <td bgcolor="#f7f9fb" style="background-color:#f7f9fb;border:1px solid {Border};border-left:3px solid {Brand};border-radius:8px;padding:14px 16px;font-family:{FontStack};font-size:14px;line-height:1.6;color:{Text};">{Multiline(text)}</td>
           </tr>
         </table>
         """;

    /// <summary>A real button.</summary>
    public static string Button(string label, string url, string? color = null)
    {
        var background = color ?? Brand;

        return $"""
           <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 16px;">
             <tr>
               <td bgcolor="{background}" style="background-color:{background};border-radius:8px;">
                 <a href="{Escape(url)}" style="display:inline-block;padding:12px 24px;font-family:{FontStack};font-size:14px;font-weight:600;color:#ffffff;text-decoration:none;border:1px solid {background};border-radius:8px;">{Escape(label)}</a>
               </td>
             </tr>
           </table>
           """;
    }

    public static string Note(string text) =>
        $"""<p style="margin:0;font-family:{FontStack};font-size:12.5px;line-height:1.6;color:{Faint};">{Escape(text)}</p>""";

    public static string RawNote(string html) =>
        $"""<p style="margin:0;font-family:{FontStack};font-size:12.5px;line-height:1.6;color:{Faint};">{html}</p>""";

    public static string Link(string text, string url) =>
        $"""<a href="{Escape(url)}" style="color:{Brand};text-decoration:underline;">{Escape(text)}</a>""";

    // ── Escaping ────────────────────────────────────────────────────────────

    /// <summary>Escapes, then turns newlines into breaks.</summary>
    public static string Multiline(string text) =>
        Escape(text).Replace("\r\n", "\n").Replace("\n", "<br />");

    public static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>Convenience for building a body out of blocks.</summary>
    public static string Compose(params string[] blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks) builder.Append(block);
        return builder.ToString();
    }
}
