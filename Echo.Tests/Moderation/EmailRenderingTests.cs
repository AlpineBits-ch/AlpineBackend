using System.Text.RegularExpressions;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Messaging;

namespace Echo.Tests.Moderation;

/// <summary>What every mail has to survive to be readable in a real client.</summary>
[TestFixture]
[Category("Unit")]
public class EmailRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ModerationAction Action(
        ModerationActionKind kind = ModerationActionKind.Ban, DateTimeOffset? expiresAt = null) =>
        ModerationAction.Create(new CreateModerationActionParams
        {
            TargetUserId = "user_target",
            ActorUserId = "user_staff",
            Kind = kind,
            Reason = ReportReason.Harassment,
            PublicNote = "You sent repeated unwanted messages after being asked to stop.",
            ExpiresAt = expiresAt,
        }, Now);

    private static SupportTicket Ticket() =>
        SupportTicket.Create(new CreateSupportTicketParams
        {
            ContactEmail = "person@example.com",
            Subject = "I cannot sign in",
            Category = SupportTicketCategory.Account,
        }, Now).Ticket;

    /// <summary>Every mail this feature can send, so a rule cannot hold for three of them and fail
    /// on the fourth.</summary>
    private static IEnumerable<TestCaseData> AllMails()
    {
        const string support = "https://support.venta.gg";

        yield return new TestCaseData(ModerationEmails.ForAction(Action(), "Sam", support).Body)
            .SetName("ban notice");

        yield return new TestCaseData(
                ModerationEmails.ForAction(Action(ModerationActionKind.Suspension, Now.AddDays(7)), "Sam", support).Body)
            .SetName("suspension notice");

        yield return new TestCaseData(
                ModerationEmails.ForAction(Action(ModerationActionKind.Unban), "Sam", support).Body)
            .SetName("restored notice");

        var appeal = ModerationAppeal.Create(new CreateAppealParams
        {
            ActionId = "mact_abc",
            ContactEmail = "person@example.com",
            Body = "I was quoting someone else.",
        }, Now);
        appeal.Decide(false, "We looked at the full thread and the messages were directed at one member.",
            "user_staff", Now);

        yield return new TestCaseData(ModerationEmails.ForAppealDecision(appeal, Action(), support).Body)
            .SetName("appeal declined");

        yield return new TestCaseData(ModerationEmails.ForTicketOpened(Ticket(), "tok_abc", support).Body)
            .SetName("ticket opened");

        yield return new TestCaseData(
                ModerationEmails.ForTicketReply(Ticket(), "Try resetting from the sign-in screen.", null, support).Body)
            .SetName("ticket reply");
    }

    // ── The rendering rules ─────────────────────────────────────────────────

    /// <summary>No <c>&lt;style&gt;</c> block anywhere.</summary>
    [TestCaseSource(nameof(AllMails))]
    public void No_mail_depends_on_a_style_block(string html)
    {
        Assert.That(html, Does.Not.Contain("<style"),
            "every declaration must be inline - a stripped stylesheet must not be able to take the "
            + "design with it");
    }

    /// <summary>
    /// The page background is set with a <c>bgcolor</c> attribute, not only with CSS.
    /// </summary>
    [TestCaseSource(nameof(AllMails))]
    public void Every_mail_sets_its_background_with_an_attribute(string html)
    {
        Assert.That(html, Does.Contain("bgcolor="),
            "a background set only in CSS on <body> is one Gmail and Outlook will drop");
    }

    /// <summary>Nothing Outlook renders as nothing or collapses.</summary>
    [TestCaseSource(nameof(AllMails))]
    public void No_mail_uses_css_outlook_ignores(string html)
    {
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("linear-gradient"), "Outlook renders it as nothing");
            Assert.That(html, Does.Not.Contain("display:flex"), "Outlook collapses it");
        });
    }

    /// <summary>Declared both ways, which is what stops the better-behaved clients auto-inverting a
    /// light design into a low-contrast dark one.</summary>
    [TestCaseSource(nameof(AllMails))]
    public void Every_mail_declares_a_light_colour_scheme(string html)
    {
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("name=\"color-scheme\""));
            Assert.That(html, Does.Contain("name=\"supported-color-schemes\""));
        });
    }

    /// <summary>Every colour the layout uses clears 4.5:1 against the surface it sits on.</summary>
    [TestCase(EmailLayout.Text)]
    [TestCase(EmailLayout.Muted)]
    [TestCase(EmailLayout.Faint)]
    [TestCase(EmailLayout.Brand)]
    [TestCase(EmailLayout.Danger)]
    [TestCase(EmailLayout.Warning)]
    [TestCase(EmailLayout.Success)]
    [TestCase(EmailLayout.Info)]
    public void Every_text_colour_is_readable_on_the_card(string colour)
    {
        var ratio = ContrastRatio(colour, EmailLayout.CardBackground);

        Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5),
            $"{colour} on {EmailLayout.CardBackground} is {ratio:F2}:1");
    }

    /// <summary>The same colours have to survive the page background too, since the footer sits on
    /// it rather than on the card.</summary>
    [TestCase(EmailLayout.Text)]
    [TestCase(EmailLayout.Muted)]
    [TestCase(EmailLayout.Faint)]
    public void Every_text_colour_is_readable_on_the_page(string colour)
    {
        var ratio = ContrastRatio(colour, EmailLayout.PageBackground);

        Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5),
            $"{colour} on {EmailLayout.PageBackground} is {ratio:F2}:1");
    }

    /// <summary>White on every accent, because that is what a button uses.</summary>
    [TestCase(EmailLayout.Brand)]
    [TestCase(EmailLayout.Danger)]
    [TestCase(EmailLayout.Warning)]
    [TestCase(EmailLayout.Success)]
    [TestCase(EmailLayout.Info)]
    public void White_button_text_is_readable_on_every_accent(string accent)
    {
        var ratio = ContrastRatio("#ffffff", accent);

        Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5), $"white on {accent} is {ratio:F2}:1");
    }

    // ── Content, not just chrome ────────────────────────────────────────────

    /// <summary>A moderator's note reaches the reader as text, not as markup.</summary>
    [Test]
    public void A_note_containing_markup_is_escaped()
    {
        var action = ModerationAction.Create(new CreateModerationActionParams
        {
            TargetUserId = "user_target",
            ActorUserId = "user_staff",
            Kind = ModerationActionKind.Ban,
            Reason = ReportReason.Spam,
            PublicNote = "<script>alert(1)</script> & \"quoted\"",
        }, Now);

        var (_, body) = ModerationEmails.ForAction(action, "Sam", "https://support.venta.gg");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Not.Contain("<script>"));
            Assert.That(body, Does.Contain("&lt;script&gt;"));
            Assert.That(body, Does.Contain("&amp;"));
        });
    }

    /// <summary>The preview line an inbox shows.</summary>
    [TestCaseSource(nameof(AllMails))]
    public void Every_mail_carries_a_preheader(string html)
    {
        Assert.That(html, Does.Contain("mso-hide:all"),
            "the hidden preview line is missing, so the inbox will scrape the badge instead");
    }

    /// <summary>A ban notice has to carry the reference and a way to use it.</summary>
    [Test]
    public void A_ban_notice_carries_its_reference_and_the_appeal_link()
    {
        var action = Action();
        var (_, body) = ModerationEmails.ForAction(action, "Sam", "https://support.venta.gg");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain(action.Reference));
            Assert.That(body, Does.Contain($"https://support.venta.gg/appeal?ref={action.Reference}"));
        });
    }

    /// <summary>An unban has nothing to appeal, so offering it would send someone to a form with
    /// nothing to say.</summary>
    [Test]
    public void A_restored_notice_offers_no_appeal()
    {
        var (_, body) = ModerationEmails.ForAction(
            Action(ModerationActionKind.Unban), "Sam", "https://support.venta.gg");

        Assert.That(body, Does.Not.Contain("/appeal?ref="));
    }

    // ── Contrast ────────────────────────────────────────────────────────────

    /// <summary>WCAG relative luminance and contrast ratio.</summary>
    private static double ContrastRatio(string foreground, string background)
    {
        var lighter = Math.Max(Luminance(foreground), Luminance(background));
        var darker = Math.Min(Luminance(foreground), Luminance(background));

        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        var match = Regex.Match(hex, "^#?([0-9a-fA-F]{6})$");
        Assert.That(match.Success, Is.True, $"'{hex}' is not a six-digit hex colour");

        var value = match.Groups[1].Value;

        double Channel(int offset)
        {
            var raw = Convert.ToInt32(value.Substring(offset, 2), 16) / 255.0;
            return raw <= 0.03928 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
