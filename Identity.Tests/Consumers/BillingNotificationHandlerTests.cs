using Alba;
using Identity.Application.Consumers;
using Identity.Application.Services;
using Identity.Application.Templates;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Consumers;

/// <summary>
/// The three billing mails: who gets one, who must never get one, and how many arrive when the bus
/// delivers the same message twice.
/// </summary>
[TestFixture]
public class BillingNotificationHandlerTests
{
    private static IAlbaHost Host => AppFixture.Host;

    private IServiceScope _scope = null!;
    private MicroserviceContext _ctx = null!;
    private RecordingSender _sender = null!;
    private BillingMailService _mail = null!;
    private BillingNotificationHandler _handler = null!;

    /// <summary>Counts sends instead of making them.</summary>
    private sealed class RecordingSender : IBillingMailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(
            string toAddress, string subject, string htmlBody, CancellationToken cancellationToken)
        {
            Sent.Add((toAddress, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    private static string TemplatesRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "Identity.Application", "Templates")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate Identity.Application/Templates");

        return Path.Combine(directory!.FullName, "Identity.Application", "Templates");
    }

    [SetUp]
    public void SetUp()
    {
        _scope = Host.Services.CreateScope();
        _ctx = _scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        _sender = new RecordingSender();
        _mail = new BillingMailService(
            _sender, new EmailTemplateRenderer(TemplatesRoot()), NullLogger<BillingMailService>.Instance);
        _handler = new BillingNotificationHandler();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (await _ctx.BillingNotifications.AnyAsync())
        {
            _ctx.BillingNotifications.RemoveRange(_ctx.BillingNotifications);
            await _ctx.SaveChangesAsync();
        }

        if (await _ctx.Users.AnyAsync())
        {
            _ctx.Users.RemoveRange(_ctx.Users);
            await _ctx.SaveChangesAsync();
        }

        _scope.Dispose();
    }

    private async Task<ApplicationUser> SeedAsync(
        UserType type = UserType.Default, UserStatus status = UserStatus.Active, bool tombstoned = false)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"bill-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"bl{Guid.NewGuid():N}"[..15],
            BirthDate = new DateOnly(1990, 1, 1),
        });

        user.UserType = type;
        user.Status = status;

        if (tombstoned) user.Tombstone();

        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        return user;
    }

    private static CreditIssuedNotification Credit(string userId, string key = "credit.issued:cred_1") => new()
    {
        UserId = userId,
        DedupeKey = key,
        Points = 1500,
        BalancePoints = 2500,
        ExpiresAt = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero),
        IssuedBy = CreditIssuedBy.Staff,
        Disclaimer =
            "Credits are a promotional balance with no cash value. They cannot be bought, refunded, "
            + "transferred or exchanged for money, and each parcel expires on the date shown.",
        OccurredAt = DateTimeOffset.UtcNow,
    };

    // ── normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task A_credit_issuance_mails_the_recipient()
    {
        var user = await SeedAsync();

        await _handler.Handle(Credit(user.Id), _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(_sender.Sent[0].To, Is.EqualTo(user.Email));
            Assert.That(_sender.Sent[0].Subject, Does.Contain("Credit"));
            Assert.That(_sender.Sent[0].Body, Does.Contain("1,500"));
            Assert.That(_sender.Sent[0].Body, Does.Contain("2,500"));
            Assert.That(_sender.Sent[0].Body, Does.Contain("14 November 2026"));
        });
    }

    /// <summary>Section 8.1: credit has no cash value, is not refundable, is not transferable, and the
    /// sentence saying so goes everywhere a balance is displayed. A currency symbol in the same mail
    /// is a regulatory problem rather than a copy problem, which is why it is asserted rather than
    /// reviewed.</summary>
    [Test]
    public async Task A_credit_mail_carries_the_disclaimer_and_no_currency_anywhere()
    {
        var user = await SeedAsync();
        var message = Credit(user.Id);

        await _handler.Handle(message, _ctx, _mail, CancellationToken.None);

        var body = _sender.Sent.Single().Body;

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain(message.Disclaimer),
                "the disclaimer travels on the message so it changes in one place");

            foreach (var symbol in new[] { "$", "€", "£", "¥", "USD", "EUR", "GBP" })
            {
                Assert.That(body, Does.Not.Contain(symbol),
                    "credit is denominated in points and is never sold - see monetization.md 8.1");
            }
        });
    }

    [Test]
    public async Task A_revoked_grant_says_something_was_removed()
    {
        var user = await SeedAsync();

        await _handler.Handle(
            new EntitlementGrantNotification
            {
                UserId = user.Id,
                DedupeKey = "grant.revoked:gran_1:9",
                Change = EntitlementGrantChange.Revoked,
                PlanDisplayName = "Pro",
                OccurredAt = DateTimeOffset.UtcNow,
            },
            _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent.Single().Body, Does.Contain("removed"));
    }

    [Test]
    public async Task A_plan_upgrade_names_both_plans()
    {
        var user = await SeedAsync();

        await _handler.Handle(
            new PlanUpgradedNotification
            {
                UserId = user.Id,
                DedupeKey = "plan.upgraded:sub_1:pro@2:x",
                PlanDisplayName = "Pro",
                PreviousPlanDisplayName = "Plus",
                CurrentPeriodEnd = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero),
                OccurredAt = DateTimeOffset.UtcNow,
            },
            _ctx, _mail, CancellationToken.None);

        var sent = _sender.Sent.Single();

        Assert.Multiple(() =>
        {
            Assert.That(sent.Subject, Does.Contain("Pro"));
            Assert.That(sent.Body, Does.Contain("Plus"));
            Assert.That(sent.Body, Does.Contain("3 September 2026"));
        });
    }

    // ── edge: the same message twice ─────────────────────────────────────────

    /// <summary>The regression this whole design exists to make impossible.</summary>
    [Test]
    public async Task A_redelivered_notification_sends_exactly_one_email()
    {
        var user = await SeedAsync();
        var message = Credit(user.Id);

        await _handler.Handle(message, _ctx, _mail, CancellationToken.None);
        await _handler.Handle(message, _ctx, _mail, CancellationToken.None);
        await _handler.Handle(message, _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Has.Count.EqualTo(1));
        Assert.That(await _ctx.BillingNotifications.CountAsync(), Is.EqualTo(1));
    }

    /// <summary>A second, genuinely different transition for the same account is a second mail. The
    /// dedupe key is the transition's identity, not the account's.</summary>
    [Test]
    public async Task Two_different_transitions_for_one_account_are_two_emails()
    {
        var user = await SeedAsync();

        await _handler.Handle(Credit(user.Id, "credit.issued:cred_1"), _ctx, _mail, CancellationToken.None);
        await _handler.Handle(Credit(user.Id, "credit.issued:cred_2"), _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Has.Count.EqualTo(2));
    }

    /// <summary>A grant with more keys than anyone will read lists none of them rather than printing a
    /// wall of identifiers.</summary>
    [Test]
    public async Task A_grant_touching_too_many_keys_does_not_list_them()
    {
        var user = await SeedAsync();

        await _handler.Handle(
            new EntitlementGrantNotification
            {
                UserId = user.Id,
                DedupeKey = "grant.issued:gran_2:1",
                Change = EntitlementGrantChange.Issued,
                Entitlements = [.. Enumerable.Range(0, BillingMailService.MaxListedEntitlements + 1)
                    .Select(index => $"feature.key_{index}")],
                OccurredAt = DateTimeOffset.UtcNow,
            },
            _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent.Single().Body, Does.Not.Contain("feature.key_0"));
    }

    // ── negative: accounts that must not be mailed ───────────────────────────

    [Test]
    public async Task A_bot_account_is_not_mailed()
    {
        var bot = await SeedAsync(UserType.Bot);

        await _handler.Handle(Credit(bot.Id), _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await _ctx.BillingNotifications.AnyAsync(), Is.False,
            "an account that cannot be mailed must not consume a claim either");
    }

    /// <summary>Tombstone nulls the address, so a naive send throws or mails an empty string.</summary>
    [Test]
    public async Task A_tombstoned_account_is_not_mailed()
    {
        var deleted = await SeedAsync(tombstoned: true);

        await _handler.Handle(Credit(deleted.Id), _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Is.Empty);
    }

    [TestCase(UserStatus.PendingDeletion)]
    [TestCase(UserStatus.PurgeInProgress)]
    [TestCase(UserStatus.Deleted)]
    public async Task An_account_on_its_way_out_is_not_mailed(UserStatus status)
    {
        var leaving = await SeedAsync(status: status);

        await _handler.Handle(Credit(leaving.Id), _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Is.Empty);
    }

    [Test]
    public async Task An_unknown_account_is_not_mailed()
    {
        await _handler.Handle(Credit("user_does_not_exist"), _ctx, _mail, CancellationToken.None);

        Assert.That(_sender.Sent, Is.Empty);
    }
}
