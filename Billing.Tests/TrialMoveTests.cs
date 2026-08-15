using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Promotions;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Wolverine;

namespace Billing.Tests;

/// <summary>Moving a live trial from one guild to another.</summary>
[TestFixture]
public class TrialMoveTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private string _originalSecretKey = string.Empty;
    private string _originalMode = string.Empty;

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PromotionCampaignService _campaigns = null!;
    private IStripeGateway _gateway = null!;
    private IMessageBus _bus = null!;
    private PromotionCampaign _campaign = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _originalSecretKey = Env.License.StripeSecretKey;
        _originalMode = Env.License.Mode;
        Env.License.StripeSecretKey = PromotionFixtures.StripeSecretKey;
        Env.License.Mode = LicenseConfiguration.Hosted;

        _clock = new TestClock(Now);
        _campaigns = PromotionFixtures.Campaigns(_db, _clock);
        _gateway = PromotionFixtures.StripeSaying("fp_move_tests");
        _bus = PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying());

        await PromotionFixtures.SeedPlansAsync(_db, Now);

        var cards = new PaymentMethodService(
            _db, _gateway, new StripeCustomerRegistry(_db, _gateway));

        await cards.CreateSetupIntentAsync(PromotionFixtures.Owner, CancellationToken.None);

        _campaign = await _campaigns.CreateAsync(
            PromotionFixtures.Open(trialDays: 30), PromotionFixtures.Staff, CancellationToken.None);

        await _db.SaveChangesAsync();
    }

    [TearDown]
    public async Task Dispose()
    {
        Env.License.StripeSecretKey = _originalSecretKey;
        Env.License.Mode = _originalMode;
        await _db.DisposeAsync();
    }

    private TrialService Trials() =>
        PromotionFixtures.Trials(_db, _clock, _gateway, _bus, _campaigns);

    private PlanService PlanWriter() =>
        new(_db, new PlanCatalogueService(_db, Plans.Catalogue()),
            new EntitlementVersionService(_db), Plans.Options(), _clock);

    /// <summary>Starts the trial and then does what the webhook does: writes the assignment, under the
    /// subscription's own <c>AssignedBy</c>. Through the same service the reconciler assigns with, so
    /// what the move finds is what a real activation would have left.</summary>
    private async Task StartAndActivateAsync()
    {
        await Trials().StartAsync(
            _campaign.Code, PromotionFixtures.Guild, PromotionFixtures.PaymentMethodId,
            PromotionFixtures.Owner, CancellationToken.None);

        await _db.SaveChangesAsync();

        await PlanWriter().AssignAsync(
            new EntitlementSubject(SubjectKind.Guild, PromotionFixtures.Guild),
            Plans.Pro,
            versionNumber: null,
            "The Stripe subscription is trialing.",
            SubscriptionReconciler.AssignedBy(PromotionFixtures.TrialSubscriptionId),
            CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<TrialMoveResult> MoveAsync(string toGuildId = PromotionFixtures.OtherGuild)
    {
        var moved = await Trials().MoveAsync(
            _campaign.Code, toGuildId, PromotionFixtures.Owner, CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return moved;
    }

    private Task<PlanAssignment?> AssignmentFor(string guildId) =>
        _db.PlanAssignments.AsNoTracking().FirstOrDefaultAsync(
            row => row.SubjectKind == SubjectKind.Guild && row.SubjectId == guildId);

    // ── The clock ────────────────────────────────────────────────────────────

    /// <summary>Ten days in, and it still ends when it was always going to.</summary>
    [Test]
    public async Task A_trial_moved_between_guilds_keeps_its_original_clock()
    {
        await StartAndActivateAsync();

        _clock.Advance(TimeSpan.FromDays(10));

        var moved = await MoveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(moved.Guild.EndsAt, Is.EqualTo(Now.AddDays(30)));
            Assert.That(moved.Guild.RedeemedAt, Is.EqualTo(Now));
        });
    }

    // ── The entitlement follows it ───────────────────────────────────────────

    [Test]
    public async Task The_plan_moves_to_the_new_guild_and_the_old_one_falls_back()
    {
        await StartAndActivateAsync();

        var moved = await MoveAsync();

        var pro = await _db.Plans.AsNoTracking().SingleAsync(row => row.Name == Plans.Pro);
        var free = await _db.Plans.AsNoTracking().SingleAsync(row => row.Name == Plans.Free);

        var gained = await AssignmentFor(PromotionFixtures.OtherGuild);
        var left = await AssignmentFor(PromotionFixtures.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(gained?.PlanId, Is.EqualTo(pro.Id));
            Assert.That(gained?.AssignedBy,
                Is.EqualTo(SubscriptionReconciler.AssignedBy(PromotionFixtures.TrialSubscriptionId)),
                "the subscription still owns the assignment, so it still takes it away when it ends");

            Assert.That(left?.PlanId, Is.EqualTo(free.Id),
                "the guild the trial left keeps a row saying free rather than losing its assignment");

            Assert.That(moved.Announcements, Has.Count.EqualTo(2),
                "both guilds have to be told their entitlements moved");
        });
    }

    /// <summary>The local row and Stripe's metadata both move.</summary>
    [Test]
    public async Task The_subscription_and_its_stripe_metadata_name_the_new_guild()
    {
        await StartAndActivateAsync();

        await MoveAsync();

        var subscription = await _db.Subscriptions.AsNoTracking().SingleAsync();

        Assert.That(subscription.SubjectId, Is.EqualTo(PromotionFixtures.OtherGuild));

        await _gateway.Received(1).UpdateSubscriptionMetadataAsync(
            PromotionFixtures.TrialSubscriptionId,
            Arg.Is<IReadOnlyDictionary<string, string>>(metadata =>
                metadata[SubscriptionReconciler.SubjectIdMetadataKey] == PromotionFixtures.OtherGuild
                && metadata[TrialService.CampaignMetadataKey] == PromotionFixtures.Campaign),
            Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>An assignment somebody else made is left exactly where it is.</summary>
    [Test]
    public async Task A_guild_on_somebody_elses_assignment_is_left_alone()
    {
        await StartAndActivateAsync();

        await PlanWriter().AssignAsync(
            new EntitlementSubject(SubjectKind.Guild, PromotionFixtures.Guild),
            Plans.Pro,
            versionNumber: null,
            "Onboarding agreement.",
            "user_staff",
            CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await MoveAsync();

        var left = await AssignmentFor(PromotionFixtures.Guild);

        Assert.That(left?.AssignedBy, Is.EqualTo("user_staff"));
    }

    // ── The refusals ─────────────────────────────────────────────────────────

    /// <summary>A double-clicked button is not an error, and it is not a Stripe write either.</summary>
    [Test]
    public async Task Moving_a_trial_to_the_guild_it_is_already_on_changes_nothing()
    {
        await StartAndActivateAsync();
        _gateway.ClearReceivedCalls();

        var moved = await MoveAsync(PromotionFixtures.Guild);

        Assert.That(moved.Announcements, Is.Empty);

        await _gateway.DidNotReceive().UpdateSubscriptionMetadataAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Moving onto a guild that is already paying for something would leave two things buying
    /// the same plan, and the second of them free.</summary>
    [Test]
    public async Task Moving_onto_a_guild_that_already_subscribes_is_refused()
    {
        await StartAndActivateAsync();

        var pro = await _db.Plans.SingleAsync(row => row.Name == Plans.Pro);

        _db.Subscriptions.Add(new Subscription
        {
            Id = Subscription.GenerateId(),
            StripeSubscriptionId = "sub_other_guild_pays",
            PayerUserId = PromotionFixtures.OtherOwner,
            SubjectKind = SubjectKind.Guild,
            SubjectId = PromotionFixtures.OtherGuild,
            PlanId = pro.Id,
            VersionNumber = 1,
            Status = SubscriptionStatus.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(async () => await MoveAsync());

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.AlreadySubscribed));
    }

    [Test]
    public void Moving_a_trial_that_was_never_taken_is_refused()
    {
        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(async () => await MoveAsync());

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.NoTrialToMove));
    }

    [Test]
    public async Task Somebody_who_does_not_manage_the_target_guild_is_refused()
    {
        await StartAndActivateAsync();

        _bus = PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(), allowed: false);

        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(async () => await MoveAsync());

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.TargetRequired));
    }

    /// <summary>An expired trial cannot be moved, because moving one would be a second trial - which is
    /// the thing the record of the first exists to prevent.</summary>
    [Test]
    public async Task Moving_a_trial_that_has_ended_is_refused()
    {
        await StartAndActivateAsync();

        _clock.Advance(TimeSpan.FromDays(31));

        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(async () => await MoveAsync());

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.NoTrialToMove));
    }
}
