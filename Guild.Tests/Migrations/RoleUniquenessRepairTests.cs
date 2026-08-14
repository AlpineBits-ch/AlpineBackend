using Guild.Domain.Enums;
using Guild.Persistence.Migrations;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RoleAggregate = Guild.Domain.Aggregates.Role;
using RoleMemberEntity = Guild.Domain.Entity.RoleMember;

namespace Guild.Tests.Migrations;

/// <summary>
/// Covers 20260814112355_RepairCounterfeitEveryoneRolesAndDuplicateRoleMembers and the pair of
/// unique indexes 20260814112640_AddRoleAndRoleMemberUniqueIndexes builds on top of it.
/// </summary>
[TestFixture]
public class RoleUniquenessRepairTests
{
    private const string EveryoneIndex = "ix_roles_guild_id_everyone";
    private const string RoleMemberIndex = "ix_role_members_role_id_member_id";

    /// <summary>The DDL 20260814112640 emits, as Postgres.</summary>
    private const string CreateEveryoneIndexSql =
        $"CREATE UNIQUE INDEX {EveryoneIndex} ON roles (guild_id) WHERE type = 'everyone';";

    private const string CreateRoleMemberIndexSql =
        $"CREATE UNIQUE INDEX {RoleMemberIndex} ON role_members (role_id, member_id);";

    private const string SecondGuildId = "guild-migration-2";

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();

        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.ExecuteAsync(connection, $"""
            DROP INDEX IF EXISTS {EveryoneIndex};
            DROP INDEX IF EXISTS {RoleMemberIndex};
            """);
    }

    /// <summary>Puts the schema back for every other fixture in the assembly, and empties the tables
    /// first so a test that deliberately left a duplicate behind cannot stop the index building.
    /// </summary>
    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await PostgresTestDatabase.ResetAsync();

        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.ExecuteAsync(connection, CreateEveryoneIndexSql);
        await MigrationSqlHarness.ExecuteAsync(connection, CreateRoleMemberIndexSql);
    }

    private static Task RepairAsync(NpgsqlConnection connection) =>
        MigrationSqlHarness.ExecuteAsync(connection,
            RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql + "\n" +
            RoleUniquenessRepair.DeduplicateRoleMembersSql);

    // ── Counterfeit @everyone roles ───────────────────────────────────────────

    [Test]
    public async Task Demotion_TwoEveryoneRolesInOneGuild_KeepsTheOldest()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        var older = DateTimeOffset.UtcNow.AddDays(-30);

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-real", 0, RoleType.Everyone, createdAt: older);
        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-counterfeit", 0, RoleType.Everyone,
            createdAt: older.AddDays(20));

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql);

        var real = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "everyone-real");
        var counterfeit = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "everyone-counterfeit");

        Assert.Multiple(() =>
        {
            Assert.That(real, Is.EqualTo(RoleType.Everyone), "the guild's original @everyone is the one to keep");
            Assert.That(counterfeit, Is.EqualTo(RoleType.None));
        });
    }

    [Test]
    public async Task Demotion_DoesNotDeleteTheCounterfeitOrItsMemberships()
    {
        // Demoted, not deleted: a counterfeit can carry channel overwrites and a membership list
        // somebody built on purpose, and both cascade away with the row.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-real", 0, RoleType.Everyone,
            createdAt: DateTimeOffset.UtcNow.AddDays(-30));
        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-counterfeit", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedMemberAsync("member-1");
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-1", "everyone-counterfeit", "member-1");

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql);

        Assert.That(await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection), Is.EqualTo(new[] { "rm-1" }));
    }

    [Test]
    public async Task Demotion_SameCreatedAt_BreaksTheTieOnId()
    {
        // Both rows created inside the same clock tick is the case an ORDER BY on created_at alone
        // leaves to the planner, and a non-deterministic repair is one that can demote a different
        // role on the primary than it did on the replica.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        var sameInstant = DateTimeOffset.UtcNow.AddDays(-1);

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-b", 0, RoleType.Everyone, createdAt: sameInstant);
        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-a", 0, RoleType.Everyone, createdAt: sameInstant);

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql);

        var first = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "everyone-a");
        var second = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "everyone-b");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(RoleType.Everyone));
            Assert.That(second, Is.EqualTo(RoleType.None));
        });
    }

    [Test]
    public async Task Demotion_IsPerGuild()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.SeedGuildAsync(SecondGuildId);

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-one", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-two", 0, RoleType.Everyone,
            guildId: SecondGuildId);

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql);

        var one = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "everyone-one");
        var two = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "everyone-two");

        Assert.Multiple(() =>
        {
            Assert.That(one, Is.EqualTo(RoleType.Everyone));
            Assert.That(two, Is.EqualTo(RoleType.Everyone),
                "one @everyone each is the healthy state, not a duplicate");
        });
    }

    [Test]
    public async Task Demotion_LeavesOrdinaryRolesAlone()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-a", 0);
        await MigrationSqlHarness.SeedRoleAsync(connection, "role-b", 0);

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql);

        var a = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "role-a");
        var b = await MigrationSqlHarness.ReadRoleTypeAsync(connection, "role-b");

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(RoleType.None));
            Assert.That(b, Is.EqualTo(RoleType.None));
        });
    }

    // ── Duplicate memberships ─────────────────────────────────────────────────

    [Test]
    public async Task Dedup_PermanentAndGuestGrant_KeepsThePermanentRow()
    {
        // The one that matters.
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-guest", "role-1", "member-1",
            expiresAt: DateTimeOffset.UtcNow.AddDays(5), createdAt: DateTimeOffset.UtcNow.AddDays(-10));
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-permanent", "role-1", "member-1",
            createdAt: DateTimeOffset.UtcNow.AddDays(-1));

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DeduplicateRoleMembersSql);

        Assert.That(await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection),
            Is.EqualTo(new[] { "rm-permanent" }),
            "the permanent grant must survive even though the guest grant is the older row");
    }

    [Test]
    public async Task Dedup_TwoGuestGrants_KeepsTheLatestExpiry()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-extended", "role-1", "member-1",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-short", "role-1", "member-1",
            expiresAt: DateTimeOffset.UtcNow.AddDays(2));

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DeduplicateRoleMembersSql);

        Assert.That(await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection),
            Is.EqualTo(new[] { "rm-extended" }), "a guest whose stay was extended keeps the extension");
    }

    [Test]
    public async Task Dedup_TwoPermanentGrants_KeepsTheOldest()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-original", "role-1", "member-1",
            createdAt: DateTimeOffset.UtcNow.AddDays(-40));
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-restacked", "role-1", "member-1",
            createdAt: DateTimeOffset.UtcNow.AddDays(-2));

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DeduplicateRoleMembersSql);

        Assert.That(await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection),
            Is.EqualTo(new[] { "rm-original" }));
    }

    [Test]
    public async Task Dedup_DistinctPairs_AreLeftAlone()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-2", 0);
        await MigrationSqlHarness.SeedMemberAsync("member-2");

        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-a", "role-1", "member-1");
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-b", "role-1", "member-2");
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-c", "role-2", "member-1");

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DeduplicateRoleMembersSql);

        Assert.That(await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection),
            Is.EquivalentTo(new[] { "rm-a", "rm-b", "rm-c" }));
    }

    [Test]
    public async Task Dedup_IsIdempotent()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-1", "role-1", "member-1");
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-2", "role-1", "member-1");

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DeduplicateRoleMembersSql);
        var first = await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection);

        await MigrationSqlHarness.ExecuteAsync(connection, RoleUniquenessRepair.DeduplicateRoleMembersSql);
        var second = await MigrationSqlHarness.ReadRoleMemberIdsAsync(connection);

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Is.EqualTo(first), "a re-run must not delete the survivor");
        });
    }

    // ── The repair and the indexes together ───────────────────────────────────

    [Test]
    public async Task Index_BeforeTheRepair_CannotBeBuilt()
    {
        // Why the repair is a migration of its own with an earlier timestamp.
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-real", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-counterfeit", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-1", "role-1", "member-1");
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-2", "role-1", "member-1");

        Assert.Multiple(() =>
        {
            Assert.That(async () => await MigrationSqlHarness.ExecuteAsync(connection, CreateEveryoneIndexSql),
                Throws.InstanceOf<PostgresException>());
            Assert.That(async () => await MigrationSqlHarness.ExecuteAsync(connection, CreateRoleMemberIndexSql),
                Throws.InstanceOf<PostgresException>());
        });
    }

    [Test]
    public async Task Index_AfterTheRepair_Builds()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-real", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-counterfeit", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-1", "role-1", "member-1");
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-2", "role-1", "member-1");

        await RepairAsync(connection);

        Assert.Multiple(() =>
        {
            Assert.That(async () => await MigrationSqlHarness.ExecuteAsync(connection, CreateEveryoneIndexSql),
                Throws.Nothing);
            Assert.That(async () => await MigrationSqlHarness.ExecuteAsync(connection, CreateRoleMemberIndexSql),
                Throws.Nothing);
        });
    }

    [Test]
    public async Task Index_OnceBuilt_RefusesASecondEveryoneRoleAndADuplicateMembership()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await SeedRoleAndMemberAsync(connection);

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-real", 0, RoleType.Everyone);
        await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-1", "role-1", "member-1");

        await RepairAsync(connection);
        await MigrationSqlHarness.ExecuteAsync(connection, CreateEveryoneIndexSql);
        await MigrationSqlHarness.ExecuteAsync(connection, CreateRoleMemberIndexSql);

        Assert.Multiple(() =>
        {
            Assert.That(async () => await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-later", 0, RoleType.Everyone),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo("23505"),
                "the application guard is a read another request can invalidate; this is what actually holds");
            Assert.That(async () => await MigrationSqlHarness.SeedRoleMemberAsync(connection, "rm-2", "role-1", "member-1"),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo("23505"));
        });
    }

    [Test]
    public async Task Index_OnceBuilt_StillAllowsManyOrdinaryRolesInAGuild()
    {
        // The index is partial for a reason: ordinary roles all share type 'none' and a guild has
        // many of them, so a plain unique (guild_id, type) would cap every guild at one role.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.ExecuteAsync(connection, CreateEveryoneIndexSql);

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-a", 0);

        Assert.That(async () => await MigrationSqlHarness.SeedRoleAsync(connection, "role-b", 0), Throws.Nothing);
    }

    // ── The model behind the DDL ──────────────────────────────────────────────

    /// <summary>Pins the literal DDL above to what the model declares, so the two cannot drift: the
    /// migration is generated from the model, and a test executing hand-written SQL that no longer
    /// matches it would pass while the deployed index was something else.</summary>
    [Test]
    public void TheModelDeclaresBothIndexes()
    {
        using var context = new PostgresGuildContext();

        var everyone = context.Model.FindEntityType(typeof(RoleAggregate))!
            .GetIndexes().Single(i => i.GetDatabaseName() == EveryoneIndex);

        var membership = context.Model.FindEntityType(typeof(RoleMemberEntity))!
            .GetIndexes().Single(i => i.GetDatabaseName() == RoleMemberIndex);

        Assert.Multiple(() =>
        {
            Assert.That(everyone.IsUnique, Is.True);
            Assert.That(everyone.GetFilter(), Is.EqualTo("type = 'everyone'"));
            Assert.That(everyone.Properties.Select(p => p.Name), Is.EqualTo(new[] { nameof(RoleAggregate.GuildId) }));
            Assert.That(membership.IsUnique, Is.True);
            Assert.That(membership.Properties.Select(p => p.Name),
                Is.EqualTo(new[] { nameof(RoleMemberEntity.RoleId), nameof(RoleMemberEntity.MemberId) }));
        });
    }

    /// <summary>The plain guild_id index the foreign-key convention would otherwise drop once the
    /// partial one leads with the same column. Losing it puts "list this guild's roles" - which is
    /// on the guild payload every client fetches - on a sequential scan.</summary>
    [Test]
    public void TheModelKeepsThePlainGuildIdIndex()
    {
        using var context = new PostgresGuildContext();

        var indexes = context.Model.FindEntityType(typeof(RoleAggregate))!.GetIndexes()
            .Where(i => !i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual([nameof(RoleAggregate.GuildId)]));

        Assert.That(indexes.Count(), Is.EqualTo(1));
    }

    private static async Task SeedRoleAndMemberAsync(NpgsqlConnection connection)
    {
        await MigrationSqlHarness.SeedRoleAsync(connection, "role-1", 0);
        await MigrationSqlHarness.SeedMemberAsync("member-1");
    }
}
