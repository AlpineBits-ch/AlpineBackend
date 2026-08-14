using AppEnvironment;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Migrations;
using Guild.Tests.Helpers;
using Npgsql;

namespace Guild.Tests.Migrations;

/// <summary>
/// Executes the bit-remap SQL against a real Postgres and checks that a pre-migration mask comes
/// out the other side meaning the same thing.
/// </summary>
[TestFixture]
public class ModulePermissionBitRemapTests
{
    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();
    }
    // ── Normal case ───────────────────────────────────────────────────────────

    [Test]
    public async Task Up_MovesEveryMappedBitToItsNewPosition()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        // Every mapped bit set at once, and nothing else.
        var preMigration = ModulePermissionBitRemap.Mapping
            .Aggregate(0ul, (mask, m) => mask | (1ul << m.OldBit));

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-all", preMigration);
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());

        var (core, module) = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-all");

        var expectedModule = ModulePermissionBitRemap.Mapping
            .Aggregate(0ul, (mask, m) => mask | (1ul << m.NewBit));

        Assert.Multiple(() =>
        {
            Assert.That((ulong)module, Is.EqualTo(expectedModule), "every module bit lands at its new position");
            Assert.That((ulong)core, Is.Zero, "and is cleared from the core mask");
        });
    }

    [Test]
    public async Task Up_LeavesCoreBitsUntouched()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        // The bits that stayed in Permissions, including bit 63 - the one that forced the column to
        // be numeric rather than bigint in the first place.
        var coreOnly = (ulong)(Permissions.ViewChannel | Permissions.SendMessages |
                               Permissions.ManageGuild | Permissions.ManageNicknames |
                               Permissions.Superadmin);

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-core", coreOnly);
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());

        var (core, module) = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-core");

        Assert.Multiple(() =>
        {
            Assert.That((ulong)core, Is.EqualTo(coreOnly), "no core bit may move or be lost");
            Assert.That((ulong)module, Is.Zero, "and nothing may appear in the module mask");
        });
    }

    [Test]
    public async Task Up_OnARealisticEveryoneMask_ProducesTheCurrentConstants()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        // The @everyone mask as it stood before the split: the core defaults plus ViewWiki (old bit
        // 23) plus the household participation bits at their old positions.
        var preMigration =
            (ulong)(Permissions.ViewChannel | Permissions.SendMessages | Permissions.EditOwnMessages |
                    Permissions.DeleteOwnMessages | Permissions.AddReactions | Permissions.AttachFiles |
                    Permissions.EmbedLinks | Permissions.CreateThreads | Permissions.SendMessagesInThreads |
                    Permissions.ManageOwnThreads | Permissions.Connect | Permissions.Speak |
                    Permissions.Stream | Permissions.CreateInvite | Permissions.ChangeNickname)
            | (1ul << 23)  // ViewWiki
            | (1ul << 40)  // AddListItems
            | (1ul << 41)  // CheckOffListItems
            | (1ul << 43)  // CompleteChores
            | (1ul << 45)  // AddExpenses
            | (1ul << 46)  // ManagePantry
            | (1ul << 47)  // CreateDecisions
            | (1ul << 48)  // VoteDecisions
            | (1ul << 55)  // PlanMeals
            | (1ul << 57); // LogMaintenance

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-everyone", preMigration, RoleType.Everyone);
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());

        var (core, module) = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-everyone");

        Assert.Multiple(() =>
        {
            Assert.That((ulong)module, Is.EqualTo((ulong)Role.DefaultEveryoneModulePermissions),
                "an old @everyone role must land on exactly today's module default");

            // The core half is the pre-parity default: this migration moves bits, it does not grant
            // any. The parity bits are added by the back-fill that runs after it.
            Assert.That(((Permissions)(ulong)core).HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(((Permissions)(ulong)core).HasFlag(Permissions.ChangeNickname), Is.True);
            Assert.That((ulong)core & (1ul << 23), Is.Zero, "old bit 23 is vacated for ReadMessageHistory");
            Assert.That((ulong)core & (1ul << 41), Is.Zero, "old bit 41 is vacated for UseVoiceActivity");
        });
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Test]
    public async Task Up_IsIdempotent()
    {
        // Each statement is guarded on the bit being set, so a re-run must be a no-op rather than
        // subtracting the bit a second time and underflowing into nonsense.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        var preMigration = (1ul << 23) | (1ul << 40) | (ulong)Permissions.ViewChannel;

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-twice", preMigration);
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());
        var first = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-twice");

        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());
        var second = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-twice");

        Assert.That(second, Is.EqualTo(first), "re-running the remap must change nothing");
    }

    [Test]
    public async Task Up_OnAnEmptyMask_DoesNothing()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-empty", 0);
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());

        var (core, module) = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-empty");

        Assert.Multiple(() =>
        {
            Assert.That((ulong)core, Is.Zero);
            Assert.That((ulong)module, Is.Zero);
        });
    }

    [Test]
    public async Task UpThenDown_RoundTripsToTheOriginalMask()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        var preMigration = ModulePermissionBitRemap.Mapping
            .Aggregate((ulong)Permissions.Superadmin, (mask, m) => mask | (1ul << m.OldBit));

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-round", preMigration);

        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.DownSql());

        var (core, module) = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-round");

        Assert.Multiple(() =>
        {
            Assert.That((ulong)core, Is.EqualTo(preMigration), "Down must restore the exact original mask");
            Assert.That((ulong)module, Is.Zero);
        });
    }

    // ── Negative case ─────────────────────────────────────────────────────────

    [Test]
    public async Task Up_DoesNotTouchBitsWithNoMapping()
    {
        // Bits 42-49 and 55-62 include positions that were never module bits (59-62 were simply
        // free). Nothing may migrate out of them.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        var unmapped = (1ul << 59) | (1ul << 60) | (1ul << 61) | (1ul << 62);

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-unmapped", unmapped);
        await MigrationSqlHarness.ExecuteAsync(connection, ModulePermissionBitRemap.UpSql());

        var (core, module) = await MigrationSqlHarness.ReadRoleMasksAsync(connection, "role-unmapped");

        Assert.Multiple(() =>
        {
            Assert.That((ulong)core, Is.EqualTo(unmapped));
            Assert.That((ulong)module, Is.Zero);
        });
    }
}
