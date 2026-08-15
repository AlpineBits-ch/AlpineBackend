using AppEnvironment;
using Billing.Application.Promotions;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Billing.Tests;

/// <summary>The wave gate.</summary>
[TestFixture]
public class PromotionFarmCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private const string OwnerFingerprint = "fp_the_same_physical_card";
    private const string FreshGuild = "gild_brand_new";

    private string _originalSecretKey = string.Empty;
    private string _originalMode = string.Empty;

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PromotionCampaignService _campaigns = null!;
    private PromotionCampaign _campaign = null!;
    private IStripeGateway _ownerStripe = null!;

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
        _ownerStripe = PromotionFixtures.StripeSaying(OwnerFingerprint);

        await PromotionFixtures.SeedPlansAsync(_db, Now);

        _campaign = await _campaigns.CreateAsync(
            PromotionFixtures.Open(), PromotionFixtures.Staff, CancellationToken.None);

        await _db.SaveChangesAsync();
    }

    [TearDown]
    public async Task Dispose()
    {
        Env.License.StripeSecretKey = _originalSecretKey;
        Env.License.Mode = _originalMode;
        await _db.DisposeAsync();
    }

    /// <summary>Adds a card the way a client does, which is also what creates the Stripe customer a
    /// fingerprint is read against.</summary>
    private async Task AttachCardAsync(IStripeGateway gateway, string owner)
    {
        var cards = new PaymentMethodService(
            _db, gateway, new StripeCustomerRegistry(_db, gateway));

        await cards.CreateSetupIntentAsync(owner, CancellationToken.None);
        await _db.SaveChangesAsync();
    }

    private Task<TrialStartResult> StartAsync(
        IStripeGateway gateway, IMessageBus bus, string owner, string guildId) =>
        PromotionFixtures.Trials(_db, _clock, gateway, bus, _campaigns)
            .StartAsync(_campaign.Code, guildId, null, owner, CancellationToken.None);

    /// <summary>The first trial, taken properly: a card on file, and a subscription in Stripe. Every
    /// farm case below is somebody trying to get a second one.</summary>
    private async Task TheFirstTrialAsync()
    {
        await AttachCardAsync(_ownerStripe, PromotionFixtures.Owner);

        await StartAsync(
            _ownerStripe,
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying()),
            PromotionFixtures.Owner,
            PromotionFixtures.Guild);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    /// <summary>The refusal a second attempt gets, or a failure saying it was allowed through.</summary>
    private PromotionRefusedException Refusal(
        IStripeGateway gateway, IMessageBus bus, string owner, string guildId)
    {
        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await StartAsync(gateway, bus, owner, guildId));

        Assert.That(refusal, Is.Not.Null, "a second trial was handed out");

        return refusal!;
    }

    // ── The account keeps its trial ──────────────────────────────────────────

    /// <summary>The headline case, and the sentence the whole wave exists for: making a new server
    /// gains nothing, because the trial is recorded against the account.</summary>
    [Test]
    public async Task A_new_guild_by_the_same_owner_gets_no_second_trial()
    {
        await TheFirstTrialAsync();

        var refusal = Refusal(
            _ownerStripe,
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying()),
            PromotionFixtures.Owner,
            FreshGuild);

        Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.AlreadyRedeemed));
    }

    /// <summary>The other half of section 7.1, and the one a cleanup job silently breaks.</summary>
    [Test]
    public async Task An_expired_redemption_still_blocks_a_re_trial()
    {
        await TheFirstTrialAsync();

        _clock.Advance(TimeSpan.FromDays(400));

        var stamped = await PromotionFixtures.Redemptions(_db, _clock, _campaigns)
            .StampExpiredAsync(100, CancellationToken.None);

        _db.ChangeTracker.Clear();

        Assert.That(stamped, Is.EqualTo(2), "the sweep is meant to have run, or this proves nothing");

        var refusal = Refusal(
            _ownerStripe,
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying()),
            PromotionFixtures.Owner,
            FreshGuild);

        Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.AlreadyRedeemed));
    }

    // ── The guild keeps its trial ────────────────────────────────────────────

    /// <summary>A guild sold, given away or handed over is still a guild that has had its trial. This
    /// is why the redemption is recorded against the guild as well as the owner: matching on the owner
    /// alone would make "transfer the server to an alt" the whole exploit.</summary>
    [Test]
    public async Task A_guild_that_changes_hands_gets_no_second_trial()
    {
        await TheFirstTrialAsync();

        // A completely different account, with no redemption of its own and nothing in common with the
        // first owner, asking for the guild the first owner already trialled.
        var refusal = Refusal(
            PromotionFixtures.StripeSaying(fingerprint: null, customerId: "cus_promo_other"),
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(
                phoneNumber: "+41799998877", deviceIds: ["device-z"])),
            PromotionFixtures.OtherOwner,
            PromotionFixtures.Guild);

        Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.GuildAlreadyRedeemed));
    }

    /// <summary>Moving a trial does not launder the guild it left back into eligibility.</summary>
    [Test]
    public async Task A_guild_a_trial_moved_away_from_gets_no_second_trial()
    {
        await TheFirstTrialAsync();

        await PromotionFixtures.Trials(_db, _clock, _ownerStripe,
                PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying()), _campaigns)
            .MoveAsync(_campaign.Code, PromotionFixtures.OtherGuild, PromotionFixtures.Owner,
                CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var refusal = Refusal(
            PromotionFixtures.StripeSaying(fingerprint: null, customerId: "cus_promo_other"),
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(
                phoneNumber: "+41799998877", deviceIds: ["device-z"])),
            PromotionFixtures.OtherOwner,
            PromotionFixtures.Guild);

        Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.GuildAlreadyRedeemed));
    }

    // ── The person keeps their trial, whatever account they use ──────────────

    [Test]
    public async Task A_new_account_sharing_a_device_gets_no_second_trial()
    {
        await TheFirstTrialAsync();

        var refusal = Refusal(
            PromotionFixtures.StripeSaying(fingerprint: null, customerId: "cus_promo_other"),
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(
                phoneNumber: "+41790000000", deviceIds: ["device-a"])),
            PromotionFixtures.OtherOwner,
            PromotionFixtures.OtherGuild);

        Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.IdentityAlreadyRedeemed));
    }

    /// <summary>An unverified number, and the name says so on purpose.</summary>
    [Test]
    public async Task A_new_account_sharing_an_unverified_phone_number_gets_no_second_trial()
    {
        await TheFirstTrialAsync();

        var refusal = Refusal(
            PromotionFixtures.StripeSaying(fingerprint: null, customerId: "cus_promo_other"),
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(
                phoneNumber: "+41 79 111 22 33", deviceIds: ["device-unrelated"])),
            PromotionFixtures.OtherOwner,
            PromotionFixtures.OtherGuild);

        Assert.Multiple(() =>
        {
            Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.IdentityAlreadyRedeemed),
                "the same number typed with spaces is the same number");
        });
    }

    /// <summary>
    /// The strongest control that actually exists, per section 7.2: a Stripe fingerprint is
    /// identical across accounts for the same physical card.
    /// </summary>
    [Test]
    public async Task A_new_account_sharing_a_card_fingerprint_gets_no_second_trial()
    {
        await TheFirstTrialAsync();

        var sameCard = PromotionFixtures.StripeSaying(
            OwnerFingerprint, customerId: "cus_promo_other");

        await AttachCardAsync(sameCard, PromotionFixtures.OtherOwner);

        var refusal = Refusal(
            sameCard,
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(
                phoneNumber: "+41790000000", deviceIds: ["device-unrelated"])),
            PromotionFixtures.OtherOwner,
            PromotionFixtures.OtherGuild);

        Assert.That(refusal.Code, Is.EqualTo(PromotionErrorCodes.IdentityAlreadyRedeemed));
    }

    // ── And the normal case still works ──────────────────────────────────────

    /// <summary>The control that refuses everybody is not a control, it is an outage.</summary>
    [Test]
    public async Task A_new_account_with_a_new_guild_is_eligible()
    {
        await TheFirstTrialAsync();

        var theirOwnCard = PromotionFixtures.StripeSaying(
            "fp_a_completely_different_card",
            subscriptionId: "sub_promo_other",
            customerId: "cus_promo_other");

        await AttachCardAsync(theirOwnCard, PromotionFixtures.OtherOwner);

        var started = await StartAsync(
            theirOwnCard,
            PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(
                phoneNumber: "+41790000000", deviceIds: ["device-of-their-own"])),
            PromotionFixtures.OtherOwner,
            PromotionFixtures.OtherGuild);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(started.Subscription.Status, Is.EqualTo("trialing"));
            Assert.That(await _db.PromotionRedemptions.CountAsync(), Is.EqualTo(4),
                "two accounts, two guilds, two rows each");
        });
    }

    /// <summary>
    /// Marks are per campaign, so a device that took last year's offer is not barred from this
    /// year's.
    /// </summary>
    [Test]
    public async Task A_device_that_redeemed_another_campaign_is_not_blocked_here()
    {
        var other = await _campaigns.CreateAsync(
            PromotionFixtures.Open(code: "pro-trial-2025"), PromotionFixtures.Staff,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        var redemption = PromotionFixtures.Seed(
            _db, other, SubjectKind.User, PromotionFixtures.Owner, PromotionFixtures.Owner,
            Now.AddDays(-400));

        PromotionFixtures.SeedMark(
            _db, other, redemption, PromotionIdentityKind.Device,
            PromotionFixtures.Hasher().Of(PromotionIdentityKind.Device, "device-shared")!,
            Now.AddDays(-400));

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var decision = await PromotionFixtures
            .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying(deviceIds: ["device-shared"]))
            .EvaluateAsync(
                _campaign, PromotionFixtures.OtherOwner, PromotionFixtures.OtherGuild, card: null,
                CancellationToken.None);

        Assert.That(decision.Allowed, Is.True, decision.Message);
    }

    /// <summary>The database's own backstop under the check that produces the readable refusal. A
    /// second row for one subject is what every farm case above is trying to create, and the index is
    /// what makes two requests racing each other unable to both win.</summary>
    [Test]
    public async Task The_database_refuses_a_second_redemption_for_one_subject()
    {
        PromotionFixtures.Seed(
            _db, _campaign, SubjectKind.User, PromotionFixtures.Owner, PromotionFixtures.Owner, Now);
        await _db.SaveChangesAsync();

        PromotionFixtures.Seed(
            _db, _campaign, SubjectKind.User, PromotionFixtures.Owner, PromotionFixtures.Owner,
            Now.AddDays(1));

        Assert.That(async () => await _db.SaveChangesAsync(),
            Throws.InstanceOf<DbUpdateException>());
    }
}
