using Billing.Domain.Aggregates;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>The credit ledger's schema, against a real Postgres.</summary>
[TestFixture]
public class CreditMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public Task Reset() => PostgresTestDatabase.ResetToEmptyAsync();

    private static CreditEntry NewEntry(
        CreditEntryKind kind = CreditEntryKind.Issue,
        long amount = 100,
        string? reason = null,
        string key = "key-1:0") => new()
    {
        Id = CreditEntry.GenerateId(),
        UserId = CreditFixtures.Buyer,
        Amount = amount,
        Kind = kind,
        IdempotencyKey = key,
        Reason = reason,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    [Test]
    public async Task The_migration_applies_to_an_empty_database_and_is_idempotent()
    {
        await using (var first = PostgresTestDatabase.CreateContext())
        {
            await first.Database.MigrateAsync();
            first.CreditEntries.Add(NewEntry());
            await first.SaveChangesAsync();
        }

        await using var second = PostgresTestDatabase.CreateContext();
        await second.Database.MigrateAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await second.Database.GetPendingMigrationsAsync(), Is.Empty);
            Assert.That(await second.CreditEntries.CountAsync(), Is.EqualTo(1));
        });
    }

    /// <summary>The same guard <c>GrantMigrationTests</c> carries, extended to the new tables:
    /// <c>CreditEntryKind</c> is text, so it can gain a member without a migration and without a
    /// service that will not start. <c>GrantSource</c> gained <c>Credit</c> in this wave on exactly
    /// that basis.</summary>
    [Test]
    public async Task The_entry_kind_is_text_and_no_postgres_enum_type_exists()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var kind = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'credit_entries' AND column_name = 'kind'");

        var enumTypes = await PostgresTestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace "
            + "WHERE t.typtype = 'e' AND n.nspname = 'public'");

        Assert.Multiple(() =>
        {
            Assert.That(kind, Is.EqualTo("text"));
            Assert.That(enumTypes, Is.Zero);
        });
    }

    /// <summary>The whole concurrency story in one index.</summary>
    [Test]
    public async Task Two_entries_cannot_share_an_idempotency_key()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.CreditEntries.Add(NewEntry(key: "spend-1:0"));
        await db.SaveChangesAsync();

        db.CreditEntries.Add(NewEntry(key: "spend-1:0"));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    [Test]
    public async Task An_adjustment_without_a_reason_is_refused_by_the_database()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.CreditEntries.Add(NewEntry(CreditEntryKind.Adjustment, 50, reason: null));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    [Test]
    public async Task A_reversal_with_a_blank_reason_is_refused_by_the_database()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.CreditEntries.Add(NewEntry(CreditEntryKind.Reversal, -50, reason: "   "));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>An issue must credit and a spend must debit, said to the database rather than trusted
    /// to every caller. A spend that credited somebody is the shape a refund would take if one were
    /// ever written by accident.</summary>
    [Test]
    public async Task The_sign_of_an_entry_has_to_match_its_kind()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.CreditEntries.Add(NewEntry(CreditEntryKind.Spend, amount: 100, key: "bad-spend:0"));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());

        db.ChangeTracker.Clear();
        db.CreditEntries.Add(NewEntry(CreditEntryKind.Issue, amount: -100, key: "bad-issue:0"));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    [Test]
    public async Task A_lot_of_zero_points_is_refused_by_the_database()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.CreditLots.Add(new CreditLot
        {
            Id = CreditLot.GenerateId(),
            UserId = CreditFixtures.Buyer,
            OriginalAmount = 0,
            ExpiresAt = Now.AddDays(365),
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>One wallet per account.</summary>
    [Test]
    public async Task An_account_cannot_have_two_wallets()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.CreditWallets.Add(Wallet());
        await db.SaveChangesAsync();

        db.CreditWallets.Add(Wallet());

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());

        static CreditWallet Wallet() => new()
        {
            Id = CreditWallet.GenerateId(),
            UserId = CreditFixtures.Buyer,
            CachedBalance = 0,
            CachedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    /// <summary>The guard on the raw SQL that takes the wallet lock.</summary>
    [Test]
    public async Task The_wallet_lock_statement_matches_the_generated_schema()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var clock = new TestClock(Now);
        var ledger = CreditFixtures.Ledger(db, clock);

        // Any operation reaches the lock as its first statement, so a name that no longer exists
        // fails here rather than in production.
        await ledger.RebuildAsync(CreditFixtures.Buyer, CancellationToken.None);

        var wallets = await PostgresTestDatabase.ScalarAsync<long>(
            $"SELECT count(*) FROM credit_wallets WHERE user_id = '{CreditFixtures.Buyer}'");

        Assert.That(wallets, Is.EqualTo(1), "the lock statement creates the row it locks");
    }

    /// <summary>The grant's new start date, round-tripped.</summary>
    [Test]
    public async Task A_grant_stores_its_start_date_and_a_null_one_reads_back_as_immediate()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.Grants.Add(Grant(null));
        db.Grants.Add(Grant(Now.AddDays(30)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.Grants.OrderBy(grant => grant.StartsAt).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.Count(grant => grant.StartsAt is null), Is.EqualTo(1));
            Assert.That(stored.Any(grant => grant.StartsAt == Now.AddDays(30)), Is.True);

            var immediate = stored.Single(grant => grant.StartsAt is null);
            var queued = stored.Single(grant => grant.StartsAt is not null);

            Assert.That(immediate.IsActiveAt(Now), Is.True);
            Assert.That(queued.IsActiveAt(Now), Is.False);
            Assert.That(queued.IsScheduledAt(Now), Is.True);
            Assert.That(queued.IsActiveAt(Now.AddDays(31)), Is.True);
        });

        static Grant Grant(DateTimeOffset? startsAt) => new()
        {
            Id = Billing.Domain.Aggregates.Grant.GenerateId(),
            SubjectKind = Echo.Entitlements.Model.SubjectKind.Guild,
            SubjectId = CreditFixtures.Guild,
            GrantKind = GrantKind.Plan,
            Plan = Plans.Pro,
            StartsAt = startsAt,
            ExpiresAt = (startsAt ?? Now).AddDays(30),
            Reason = "Bought with credit.",
            Source = GrantSource.Credit,
            CreatedBy = CreditFixtures.Buyer,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    /// <summary>The enum member the wave needed, stored as the text the column already was. Asserted
    /// against the database because a member the database does not know is a service that crashes at
    /// startup, and only a real Postgres can show that it does.</summary>
    [Test]
    public async Task A_grant_can_be_sourced_from_credit()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        db.Grants.Add(new Grant
        {
            Id = Grant.GenerateId(),
            SubjectKind = Echo.Entitlements.Model.SubjectKind.Guild,
            SubjectId = CreditFixtures.Guild,
            GrantKind = GrantKind.Plan,
            Plan = Plans.Pro,
            ExpiresAt = Now.AddDays(30),
            Reason = "500 credits spent.",
            Source = GrantSource.Credit,
            CreatedBy = CreditFixtures.Buyer,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.Grants.SingleAsync();
        var text = await PostgresTestDatabase.ScalarAsync<string>("SELECT source FROM grants LIMIT 1");

        Assert.Multiple(() =>
        {
            Assert.That(stored.Source, Is.EqualTo(GrantSource.Credit));
            Assert.That(text, Is.EqualTo("Credit"));
        });
    }
}
