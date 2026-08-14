using Billing.Application.Services;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>The per-subject entitlement version.</summary>
[TestFixture]
public class EntitlementVersionTests
{
    private MicroserviceContext _db = null!;
    private EntitlementVersionService _versions = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _versions = new EntitlementVersionService(_db);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    /// <summary>Most subjects have never had a grant.</summary>
    [Test]
    public async Task A_subject_nothing_has_ever_changed_is_at_version_zero()
    {
        var version = await _versions.VersionAsync(Subjects.Guild);

        Assert.Multiple(async () =>
        {
            Assert.That(version, Is.Zero);
            Assert.That(await _db.EntitlementVersions.AnyAsync(), Is.False);
        });
    }

    /// <summary>The raw <c>INSERT ...</summary>
    [Test]
    public async Task The_advance_statement_matches_the_generated_schema()
    {
        var first = await _versions.AdvanceAsync(Subjects.Guild, CancellationToken.None);
        var second = await _versions.AdvanceAsync(Subjects.Guild, CancellationToken.None);
        var third = await _versions.AdvanceAsync(Subjects.Guild, CancellationToken.None);

        var read = await _versions.VersionAsync(Subjects.Guild);
        var rows = await _db.EntitlementVersions.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(2));
            Assert.That(third, Is.EqualTo(3));
            Assert.That(read, Is.EqualTo(3), "The read and the advance have to agree.");
            Assert.That(rows, Is.EqualTo(1), "One row per subject, upserted rather than appended.");
        });
    }

    [Test]
    public async Task Each_subject_has_its_own_counter()
    {
        await _versions.AdvanceAsync(Subjects.Guild, CancellationToken.None);
        await _versions.AdvanceAsync(Subjects.Guild, CancellationToken.None);
        await _versions.AdvanceAsync(Subjects.User, CancellationToken.None);

        var guild = await _versions.VersionAsync(Subjects.Guild);
        var user = await _versions.VersionAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(guild, Is.EqualTo(2));
            Assert.That(user, Is.EqualTo(1));
        });
    }

    /// <summary>A user and a guild that happen to share an id string are different subjects. The
    /// unique index is over both columns for this reason, and an index over the id alone would make
    /// one of them silently inherit the other's version.</summary>
    [Test]
    public async Task A_user_and_a_guild_with_the_same_id_do_not_share_a_counter()
    {
        var shared = "01JQZZZZZZZZZZZZZZZZZZZZZZ";

        await _versions.AdvanceAsync(new EntitlementSubject(SubjectKind.Guild, shared), CancellationToken.None);
        await _versions.AdvanceAsync(new EntitlementSubject(SubjectKind.Guild, shared), CancellationToken.None);

        var user = await _versions.VersionAsync(new EntitlementSubject(SubjectKind.User, shared));

        Assert.That(user, Is.Zero);
    }

    /// <summary>The reason this is one statement rather than a read and a write.</summary>
    [Test]
    public async Task Concurrent_advances_never_hand_out_the_same_version_twice()
    {
        const int concurrency = 20;

        var advances = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            // A context each, because that is what concurrent requests actually have and because a
            // DbContext is not thread safe. The statement is what serialises them, not the caller.
            await using var context = PostgresTestDatabase.CreateContext();
            return await new EntitlementVersionService(context)
                .AdvanceAsync(Subjects.Guild, CancellationToken.None);
        });

        var issued = await Task.WhenAll(advances);

        Assert.Multiple(() =>
        {
            Assert.That(issued.Distinct().Count(), Is.EqualTo(concurrency), "A version was handed out twice.");
            Assert.That(issued.Order(), Is.EqualTo(Enumerable.Range(1, concurrency).Select(n => (long)n)));
        });
    }
}
