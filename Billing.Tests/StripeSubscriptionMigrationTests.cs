using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Billing.Tests;

/// <summary>The Stripe tables, against a real Postgres.</summary>
[TestFixture]
public class StripeSubscriptionMigrationTests
{
    private const string GuildId = "gild_01JQZZZZZZZZZZZZZZZZZZZZZZ";

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public Task Reset() => PostgresTestDatabase.ResetToEmptyAsync();

    [Test]
    public async Task The_migration_applies_and_a_subscription_round_trips()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var plan = await SeedPlanAsync(context);

        context.Subscriptions.Add(NewSubscription(plan.Id));
        context.StripeCustomers.Add(new StripeCustomer
        {
            Id = StripeCustomer.GenerateId(),
            UserId = "user_payer",
            StripeCustomerId = "cus_test",
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.Subscriptions.SingleAsync();
        var pending = await context.Database.GetPendingMigrationsAsync();
        var customers = await context.StripeCustomers.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.Empty);
            Assert.That(stored.Status, Is.EqualTo(SubscriptionStatus.Active));
            Assert.That(stored.SubjectKind, Is.EqualTo(SubjectKind.Guild));
            Assert.That(stored.IsLive, Is.True);
            Assert.That(stored.GracePeriodEndsAt, Is.Null);
            Assert.That(customers, Is.EqualTo(1));
        });
    }

    /// <summary>The double-charge guard.</summary>
    [Test]
    public async Task A_subject_cannot_hold_two_live_subscriptions()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var plan = await SeedPlanAsync(context);

        context.Subscriptions.Add(NewSubscription(plan.Id, "sub_first"));
        await context.SaveChangesAsync();

        context.Subscriptions.Add(NewSubscription(plan.Id, "sub_second", SubscriptionStatus.Trialing));

        Assert.That(async () => await context.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>The other half, and the reason the index is filtered rather than plain.</summary>
    [Test]
    public async Task A_dead_subscription_does_not_block_a_new_one_for_the_same_subject()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var plan = await SeedPlanAsync(context);

        context.Subscriptions.Add(NewSubscription(plan.Id, "sub_cancelled", SubscriptionStatus.Canceled));
        await context.SaveChangesAsync();

        context.Subscriptions.Add(NewSubscription(plan.Id, "sub_new"));
        await context.SaveChangesAsync();

        Assert.That(await context.Subscriptions.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task Two_rows_cannot_share_a_stripe_subscription_id()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var plan = await SeedPlanAsync(context);

        var first = NewSubscription(plan.Id, "sub_same", SubscriptionStatus.Canceled);
        var second = NewSubscription(plan.Id, "sub_same", SubscriptionStatus.Canceled);
        second.SubjectId = "gild_someone_else";

        context.Subscriptions.Add(first);
        await context.SaveChangesAsync();

        context.Subscriptions.Add(second);

        Assert.That(async () => await context.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>
    /// The property the whole webhook design rests on: the insert <em>is</em> the duplicate check, so
    /// a second delivery of the same event has to be refused by the database rather than by a
    /// read-then-act check that two replicas can both pass.
    /// </summary>
    [Test]
    public async Task The_same_stripe_event_cannot_be_recorded_twice()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        context.ProcessedStripeEvents.Add(NewEvent());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        context.ProcessedStripeEvents.Add(NewEvent());

        Assert.That(async () => await context.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    [Test]
    public async Task A_user_cannot_have_two_stripe_customers()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        context.StripeCustomers.Add(new StripeCustomer
        {
            Id = StripeCustomer.GenerateId(),
            UserId = "user_payer",
            StripeCustomerId = "cus_one",
        });

        await context.SaveChangesAsync();

        context.StripeCustomers.Add(new StripeCustomer
        {
            Id = StripeCustomer.GenerateId(),
            UserId = "user_payer",
            StripeCustomerId = "cus_two",
        });

        Assert.That(async () => await context.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>Same guard as <c>GrantMigrationTests</c>: a Postgres enum type cannot gain a label
    /// without a migration, and <c>SubscriptionStatus</c> is the one enum in this service that is
    /// guaranteed to grow, because Stripe decides when.</summary>
    [Test]
    public async Task The_status_column_is_text_and_no_postgres_enum_type_exists()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var status = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'subscriptions' AND column_name = 'status'");

        var enumTypes = await PostgresTestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace "
            + "WHERE t.typtype = 'e' AND n.nspname = 'public'");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo("text"));
            Assert.That(enumTypes, Is.Zero);
        });
    }

    /// <summary>
    /// The filter is raw SQL comparing against what <c>HasConversion&lt;string&gt;()</c> writes, so
    /// the three names in the index and the three members of <c>SubscriptionStatuses.Live</c> have to
    /// agree letter for letter. Nothing in C# checks that, which is why it is checked here.
    /// </summary>
    [Test]
    public async Task The_index_filter_names_the_same_statuses_the_domain_calls_live()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var definition = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes "
            + "WHERE tablename = 'subscriptions' "
            + "AND indexname = 'ix_subscriptions_subject_kind_subject_id'");

        Assert.That(definition, Is.Not.Null);

        Assert.Multiple(() =>
        {
            foreach (var status in SubscriptionStatuses.Live)
            {
                Assert.That(definition, Does.Contain($"'{status}'"),
                    $"The partial index does not name '{status}', so a second live subscription in "
                    + "that status would be accepted.");
            }
        });
    }

    /// <summary>The reverse lookup a webhook arrives needing: Stripe reports a price id and nothing
    /// else that identifies the subject's plan.</summary>
    [Test]
    public async Task A_stripe_price_id_resolves_back_to_its_plan_and_version()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var plan = await SeedPlanAsync(context, priceId: "price_v1");
        var sync = new StripeCatalogueSync(context, Substitute.For<IStripeGateway>());

        var resolved = await sync.ResolvePriceAsync("price_v1", CancellationToken.None);
        var missing = await sync.ResolvePriceAsync("price_nothing_here", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Plan.Id, Is.EqualTo(plan.Id));
            Assert.That(resolved.Version.VersionNumber, Is.EqualTo(1));

            // A price we never created is not an error and not an exception; it is an answer the
            // caller has to handle, because a Stripe account can hold objects this instance did not
            // make.
            Assert.That(missing, Is.Null);
        });
    }

    private static async Task<Plan> SeedPlanAsync(MicroserviceContext context, string? priceId = null)
    {
        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = "pro",
            DisplayName = "Pro",
            CurrentVersionNumber = 1,
            CreatedBy = "user_admin",
            StripeProductId = priceId is null ? null : "prod_test",
        };

        context.Plans.Add(plan);
        context.PlanVersions.Add(new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = "{\"voice.max_participants\":\"75\"}",
            PriceMinorUnits = 2900,
            Currency = "usd",
            StripePriceId = priceId,
            Reason = "The launch tier.",
            CreatedBy = "user_admin",
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return plan;
    }

    private static Subscription NewSubscription(
        string planId,
        string stripeId = "sub_test",
        SubscriptionStatus status = SubscriptionStatus.Active) => new()
    {
        Id = Subscription.GenerateId(),
        StripeSubscriptionId = stripeId,
        PayerUserId = "user_payer",
        SubjectKind = SubjectKind.Guild,
        SubjectId = GuildId,
        PlanId = planId,
        VersionNumber = 1,
        Status = status,
        CurrentPeriodEnd = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
        CancelAtPeriodEnd = false,
        LatestInvoiceId = "in_test",
    };

    private static ProcessedStripeEvent NewEvent() => new()
    {
        EventId = "evt_01JQZZZZZZZZZZZZZZZZZZZZZZ",
        Type = "customer.subscription.updated",
        ReceivedAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
    };
}
