using AppEnvironment;
using Billing.Application.Credit;
using Billing.Application.Dtos;
using Billing.Application.Endpoints;
using Billing.Application.Notifications;
using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Identity.Contracts.Bus.Commands;

namespace Billing.Tests;

/// <summary>
/// Which billing changes raise an email intent, and - far more importantly - which do not.
/// </summary>
[TestFixture]
public class BillingNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private string _mode = null!;

    /// <summary>Billing only runs hosted - <c>Program.cs</c> refuses to start otherwise - but
    /// <c>LICENSE_MODE</c> defaults to selfhost, so a test process that does not say so is a test
    /// process where every factory here correctly returns null and nothing is being tested.</summary>
    [SetUp]
    public void SetUp()
    {
        _mode = Env.License.Mode;
        Env.License.Mode = LicenseConfiguration.Hosted;
    }

    [TearDown]
    public void TearDown() => Env.License.Mode = _mode;

    // ── Credit issued ────────────────────────────────────────────────────────

    private static CreditLedgerResult Issued(long amount, long balance, bool replay = false) =>
        new(
            [
                new CreditEntry
                {
                    Id = "cred_one",
                    UserId = "user_recipient",
                    Amount = amount,
                    Kind = CreditEntryKind.Issue,
                    LotId = "clot_one",
                    IdempotencyKey = "key",
                },
            ],
            balance,
            replay);

    private static IReadOnlyList<CreditLotRemainder> Lots(DateTimeOffset expiresAt) =>
        [new CreditLotRemainder("clot_one", expiresAt, 500, 500, null)];

    [Test]
    public void An_issuance_tells_the_recipient_what_they_got_and_what_they_now_hold()
    {
        var notice = BillingNotices.ForCreditIssue(
            "user_recipient", Issued(500, 1200), Lots(Now.AddDays(90)), campaign: null, Now);

        Assert.That(notice, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(notice!.UserId, Is.EqualTo("user_recipient"));
            Assert.That(notice.Points, Is.EqualTo(500));
            Assert.That(notice.BalancePoints, Is.EqualTo(1200));
            Assert.That(notice.ExpiresAt, Is.EqualTo(Now.AddDays(90)));
            Assert.That(notice.IssuedBy, Is.EqualTo(CreditIssuedBy.Staff));
            Assert.That(notice.DedupeKey, Is.EqualTo("credit.issued:cred_one"));
            Assert.That(notice.Disclaimer, Is.EqualTo(CreditDisclaimer.Text));
        });
    }

    [Test]
    public void An_issuance_under_a_campaign_says_so()
    {
        var notice = BillingNotices.ForCreditIssue(
            "user_recipient", Issued(500, 500), Lots(Now.AddDays(90)), "launch-week", Now);

        Assert.That(notice!.IssuedBy, Is.EqualTo(CreditIssuedBy.Campaign));
    }

    /// <summary>The negative that matters most on this route.</summary>
    [Test]
    public void A_replayed_issuance_mails_nobody()
    {
        var notice = BillingNotices.ForCreditIssue(
            "user_recipient", Issued(500, 500, replay: true), Lots(Now.AddDays(90)), null, Now);

        Assert.That(notice, Is.Null);
    }

    [Test]
    public void A_write_with_no_issue_line_mails_nobody()
    {
        var adjustmentOnly = new CreditLedgerResult(
            [
                new CreditEntry
                {
                    Id = "cred_adj",
                    UserId = "user_recipient",
                    Amount = -100,
                    Kind = CreditEntryKind.Adjustment,
                    LotId = "clot_one",
                    IdempotencyKey = "key",
                },
            ],
            400,
            WasReplay: false);

        Assert.That(
            BillingNotices.ForCreditIssue("user_recipient", adjustmentOnly, [], null, Now),
            Is.Null);
    }

    /// <summary>Nothing in this package exists on a self-hosted instance, where everything already
    /// resolves to its maximum and Billing is not deployed at all.</summary>
    [Test]
    public void Selfhost_raises_no_billing_mail_of_any_kind()
    {
        Env.License.Mode = LicenseConfiguration.SelfHost;

        var grant = GrantFixtures.PlanGrant(subjectKind: SubjectKind.User, subjectId: "user_recipient");

        Assert.Multiple(() =>
        {
            Assert.That(
                BillingNotices.ForCreditIssue("user_recipient", Issued(500, 500), Lots(Now), null, Now),
                Is.Null);
            Assert.That(
                BillingNotices.ForGrant(EntitlementGrantChange.Issued, grant, Announcement(grant), null, Now),
                Is.Null);
            Assert.That(
                BillingNotices.ForPlanChange("user_payer", Subscription("plus", 500), Subscription("pro", 900), Now),
                Is.Null);
        });
    }

    // ── Grants ───────────────────────────────────────────────────────────────

    private static EntitlementsChanged Announcement(Grant grant, long version = 7) => new()
    {
        SubjectKind = grant.SubjectKind,
        SubjectId = grant.SubjectId,
        Reason = EntitlementsChangedReason.GrantIssued,
        GrantId = grant.Id,
        Version = version,
        ChangedKeys = ["guild.emoji_slots", "voice.max_participants"],
        OccurredAt = Now,
    };

    [Test]
    public void A_hand_made_grant_on_a_user_tells_them_what_they_were_given()
    {
        var grant = GrantFixtures.PlanGrant(
            subjectKind: SubjectKind.User, subjectId: "user_recipient", expiresAt: Now.AddDays(30));

        var notice = BillingNotices.ForGrant(
            EntitlementGrantChange.Issued, grant, Announcement(grant), planDisplayName: "Pro", Now);

        Assert.That(notice, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(notice!.UserId, Is.EqualTo("user_recipient"));
            Assert.That(notice.Change, Is.EqualTo(EntitlementGrantChange.Issued));
            Assert.That(notice.PlanDisplayName, Is.EqualTo("Pro"));
            Assert.That(notice.ExpiresAt, Is.EqualTo(Now.AddDays(30)));
            Assert.That(notice.Entitlements, Is.EquivalentTo(new[] { "guild.emoji_slots", "voice.max_participants" }));
        });
    }

    /// <summary>A revocation ended the grant now.</summary>
    [Test]
    public void A_revocation_does_not_carry_the_expiry_it_no_longer_has()
    {
        var grant = GrantFixtures.PlanGrant(
            subjectKind: SubjectKind.User, subjectId: "user_recipient", expiresAt: Now.AddDays(30));

        var notice = BillingNotices.ForGrant(
            EntitlementGrantChange.Revoked, grant, Announcement(grant), null, Now);

        Assert.That(notice!.ExpiresAt, Is.Null);
    }

    /// <summary>Billing holds no guild ownership and must not start holding any, so a guild grant has
    /// no recipient it is allowed to name.</summary>
    [Test]
    public void A_grant_on_a_guild_mails_nobody()
    {
        var grant = GrantFixtures.PlanGrant(subjectKind: SubjectKind.Guild);

        Assert.That(
            BillingNotices.ForGrant(EntitlementGrantChange.Issued, grant, Announcement(grant), null, Now),
            Is.Null);
    }

    /// <summary>Two amendments to one grant are two things a person did and get two mails; the same
    /// amendment redelivered carries the key it was built with and is stopped by the sent record on
    /// the far side.</summary>
    [Test]
    public void Two_changes_to_one_grant_are_two_distinct_dedupe_keys()
    {
        var grant = GrantFixtures.PlanGrant(subjectKind: SubjectKind.User, subjectId: "user_recipient");

        var first = BillingNotices.ForGrant(
            EntitlementGrantChange.Amended, grant, Announcement(grant, version: 7), null, Now);
        var second = BillingNotices.ForGrant(
            EntitlementGrantChange.Amended, grant, Announcement(grant, version: 8), null, Now);
        var repeat = BillingNotices.ForGrant(
            EntitlementGrantChange.Amended, grant, Announcement(grant, version: 7), null, Now.AddHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(first!.DedupeKey, Is.Not.EqualTo(second!.DedupeKey));
            Assert.That(repeat!.DedupeKey, Is.EqualTo(first.DedupeKey),
                "the key identifies the transition, not the moment the message was built");
        });
    }

    // ── Plan changes ─────────────────────────────────────────────────────────

    private static SubscriptionDto Subscription(
        string plan, long? price, string? currency = "usd", string id = "sub_1") =>
        new(
            id,
            SubjectKind.Guild,
            "gild_one",
            plan,
            plan.ToUpperInvariant(),
            2,
            "active",
            Now.AddDays(20),
            false,
            null,
            price,
            currency,
            "month",
            true);

    [Test]
    public void Moving_up_a_plan_tells_the_payer_what_they_moved_from_and_to()
    {
        var notice = BillingNotices.ForPlanChange(
            "user_payer", Subscription("plus", 500), Subscription("pro", 900), Now);

        Assert.That(notice, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(notice!.UserId, Is.EqualTo("user_payer"));
            Assert.That(notice.PreviousPlanDisplayName, Is.EqualTo("PLUS"));
            Assert.That(notice.PlanDisplayName, Is.EqualTo("PRO"));
            Assert.That(notice.CurrentPeriodEnd, Is.EqualTo(Now.AddDays(20)));
        });
    }

    [Test]
    public void Moving_down_a_plan_mails_nobody()
    {
        Assert.That(
            BillingNotices.ForPlanChange("user_payer", Subscription("pro", 900), Subscription("plus", 500), Now),
            Is.Null);
    }

    [Test]
    public void A_sideways_move_at_the_same_price_mails_nobody()
    {
        Assert.That(
            BillingNotices.ForPlanChange("user_payer", Subscription("pro", 900), Subscription("pro2", 900), Now),
            Is.Null);
    }

    /// <summary>Two currencies are not comparable, so "did this go up" has no answer and the mail that
    /// would be sent is a guess. The failure mode of guessing is telling somebody they were upgraded
    /// when they were not.</summary>
    [Test]
    public void A_cross_currency_move_mails_nobody()
    {
        Assert.That(
            BillingNotices.ForPlanChange(
                "user_payer", Subscription("plus", 500, "usd"), Subscription("pro", 900, "eur"), Now),
            Is.Null);
    }

    [Test]
    public void A_move_between_unpriced_plans_mails_nobody()
    {
        Assert.That(
            BillingNotices.ForPlanChange("user_payer", Subscription("plus", null), Subscription("pro", null), Now),
            Is.Null);
    }
}
