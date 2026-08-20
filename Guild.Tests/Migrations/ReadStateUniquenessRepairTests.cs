using Guild.Persistence.Migrations;
using Guild.Tests.Helpers;
using Npgsql;

namespace Guild.Tests.Migrations;

/// <summary>
/// Covers 20260820125502_AddReadStateUniqueIndex: the repair that collapses stacked read states,
/// and the index it clears the way for.
/// </summary>
[TestFixture]
public class ReadStateUniquenessRepairTests
{
    private const string ReadStateIndex = "ix_read_states_member_id_channel_id";

    /// <summary>The DDL the migration emits, as Postgres.</summary>
    private const string CreateReadStateIndexSql =
        $"CREATE UNIQUE INDEX {ReadStateIndex} ON read_states (member_id, channel_id);";

    private const string MemberId = "memb-migration";
    private const string ChannelId = "chan-migration";

    private static readonly DateTimeOffset Head = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Older = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();
        await MigrationSqlHarness.SeedMemberAsync(MemberId);
        await MigrationSqlHarness.SeedChannelAsync(ChannelId);

        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.ExecuteAsync(connection, $"DROP INDEX IF EXISTS {ReadStateIndex};");
    }

    /// <summary>Puts the schema back for every other fixture in the assembly, and empties the tables
    /// first so a test that deliberately left a duplicate behind cannot stop the index building.
    /// </summary>
    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await PostgresTestDatabase.ResetAsync();

        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.ExecuteAsync(connection, CreateReadStateIndexSql);
    }

    private static Task RepairAsync(NpgsqlConnection connection) =>
        MigrationSqlHarness.ExecuteAsync(connection, ReadStateUniquenessRepair.DeduplicateReadStatesSql);

    // ── The repair ────────────────────────────────────────────────────────────

    [Test]
    public async Task Deduplicate_TwoRowsForOnePair_KeepsTheFurthestAlongCursor()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-stale", MemberId, ChannelId, Older);
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-current", MemberId, ChannelId, Head);

        await RepairAsync(connection);

        Assert.That(await MigrationSqlHarness.ReadReadStateIdsAsync(connection), Is.EqualTo(new[] { "reta-current" }));
    }

    /// <summary>The row the losing ack inserted never got a timestamp written to it, and that is the
    /// one keeping the channel unread.</summary>
    [Test]
    public async Task Deduplicate_ARowThatWasNeverAcked_LosesToOneThatWas()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-null", MemberId, ChannelId, null);
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-acked", MemberId, ChannelId, Older);

        await RepairAsync(connection);

        Assert.That(await MigrationSqlHarness.ReadReadStateIdsAsync(connection), Is.EqualTo(new[] { "reta-acked" }));
    }

    [Test]
    public async Task Deduplicate_LeavesOnePairAlone()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedChannelAsync("chan-migration-2");
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-a", MemberId, ChannelId, Head);
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-b", MemberId, "chan-migration-2", Older);

        await RepairAsync(connection);

        Assert.That(await MigrationSqlHarness.ReadReadStateIdsAsync(connection),
            Is.EqualTo(new[] { "reta-a", "reta-b" }));
    }

    // ── The index ─────────────────────────────────────────────────────────────

    [Test]
    public async Task AfterTheRepair_TheIndexBuilds()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-stale", MemberId, ChannelId, Older);
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-current", MemberId, ChannelId, Head);

        await RepairAsync(connection);

        Assert.DoesNotThrowAsync(() => MigrationSqlHarness.ExecuteAsync(connection, CreateReadStateIndexSql));
    }

    [Test]
    public async Task WithoutTheRepair_TheIndexRefusesToBuild()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-stale", MemberId, ChannelId, Older);
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-current", MemberId, ChannelId, Head);

        await Assert.ThatAsync(() => MigrationSqlHarness.ExecuteAsync(connection, CreateReadStateIndexSql),
            Throws.TypeOf<PostgresException>());
    }

    [Test]
    public async Task TheIndex_RefusesASecondRowForThePair()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.ExecuteAsync(connection, CreateReadStateIndexSql);
        await MigrationSqlHarness.SeedReadStateAsync(connection, "reta-first", MemberId, ChannelId, Older);

        await Assert.ThatAsync(
            () => MigrationSqlHarness.SeedReadStateAsync(connection, "reta-second", MemberId, ChannelId, Head),
            Throws.TypeOf<PostgresException>());
    }
}
