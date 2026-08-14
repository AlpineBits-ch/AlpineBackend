using Billing.Domain.Aggregates;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>The first migration for <c>billing_db</c>, against a real Postgres.</summary>
[TestFixture]
public class GrantMigrationTests
{
    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public Task Reset() => PostgresTestDatabase.ResetToEmptyAsync();

    [Test]
    public async Task Migration_applies_to_an_empty_database()
    {
        await using var context = PostgresTestDatabase.CreateContext();

        await context.Database.MigrateAsync();

        var pending = await context.Database.GetPendingMigrationsAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.Empty);
            Assert.That(applied, Is.Not.Empty);
        });

        var grant = NewGrant();
        context.Grants.Add(grant);
        await context.SaveChangesAsync();

        Assert.That(await context.Grants.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Migration_is_idempotent_on_a_second_run()
    {
        await using (var first = PostgresTestDatabase.CreateContext())
        {
            await first.Database.MigrateAsync();
            first.Grants.Add(NewGrant());
            await first.SaveChangesAsync();
        }

        await using var second = PostgresTestDatabase.CreateContext();

        // The same call UseInfrastructure makes on every start, so this is exactly what a restart
        // does. It must change nothing and must not lose the row that was already there.
        await second.Database.MigrateAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await second.Database.GetPendingMigrationsAsync(), Is.Empty);
            Assert.That(await second.Grants.CountAsync(), Is.EqualTo(1));
        });
    }

    /// <summary>The guard against <c>HasPostgresEnum</c> coming back.</summary>
    [Test]
    public async Task Enum_columns_are_text_and_no_postgres_enum_type_exists()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var subjectKind = await ColumnTypeAsync("subject_kind");
        var grantKind = await ColumnTypeAsync("grant_kind");
        var source = await ColumnTypeAsync("source");

        // 'e' is Postgres' catalogue code for an enum type. Any row means somebody created one.
        var enumTypes = await PostgresTestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace "
            + "WHERE t.typtype = 'e' AND n.nspname = 'public'");

        Assert.Multiple(() =>
        {
            Assert.That(subjectKind, Is.EqualTo("text"));
            Assert.That(grantKind, Is.EqualTo("text"));
            Assert.That(source, Is.EqualTo("text"));
            Assert.That(enumTypes, Is.Zero);
        });
    }

    [Test]
    public async Task A_permanent_grant_stores_a_null_expiry_and_reads_back_as_one()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var grant = NewGrant();
        grant.ExpiresAt = null;
        context.Grants.Add(grant);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.Grants.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.ExpiresAt, Is.Null);
            Assert.That(stored.RevokedAt, Is.Null);
            Assert.That(stored.Source, Is.EqualTo(GrantSource.Staff));
            Assert.That(stored.SubjectKind, Is.EqualTo(SubjectKind.Guild));
        });
    }

    [Test]
    public async Task A_grant_without_a_reason_is_refused_by_the_database()
    {
        await using var context = PostgresTestDatabase.CreateContext();
        await context.Database.MigrateAsync();

        var grant = NewGrant();
        grant.Reason = null!;
        context.Grants.Add(grant);

        // Required in the spec and therefore NOT NULL in the schema, rather than only validated in
        // a service somewhere.
        Assert.That(async () => await context.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    private static async Task<string?> ColumnTypeAsync(string column) =>
        await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + $"WHERE table_name = 'grants' AND column_name = '{column}'");

    private static Grant NewGrant() => new()
    {
        Id = Grant.GenerateId(),
        SubjectKind = SubjectKind.Guild,
        SubjectId = "gild_01JQZZZZZZZZZZZZZZZZZZZZZZ",
        GrantKind = GrantKind.Plan,
        Plan = "Pro",
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
        Reason = "Compensation for the outage on the 3rd.",
        Source = GrantSource.Staff,
        CreatedBy = "user_01JQZZZZZZZZZZZZZZZZZZZZZZ",
    };
}
