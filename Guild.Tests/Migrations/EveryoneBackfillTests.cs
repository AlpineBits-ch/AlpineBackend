using AppEnvironment;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Migrations;
using Guild.Tests.Helpers;
using Npgsql;

namespace Guild.Tests.Migrations;

/// <summary>
/// Covers the @everyone back-fill in 20260814090726_RemapModulePermissionBitsAndBackfillEveryone
/// against a stale row.
/// </summary>
[TestFixture]
public class EveryoneBackfillTests
{
    /// <summary>The 2^n values the migration adds, kept here as literals on purpose.</summary>
    private const string BackfillSql = """
        UPDATE roles SET permissions = permissions + 8388608
        WHERE (floor(permissions / 8388608) % 2) = 0 AND type = 'everyone';

        UPDATE roles SET permissions = permissions + 16777216
        WHERE (floor(permissions / 16777216) % 2) = 0 AND type = 'everyone';

        UPDATE roles SET permissions = permissions + 33554432
        WHERE (floor(permissions / 33554432) % 2) = 0 AND type = 'everyone';

        UPDATE roles SET permissions = permissions + 67108864
        WHERE (floor(permissions / 67108864) % 2) = 0 AND type = 'everyone';

        UPDATE roles SET permissions = permissions + 134217728
        WHERE (floor(permissions / 134217728) % 2) = 0 AND type = 'everyone';

        UPDATE roles SET permissions = permissions + 536870912
        WHERE (floor(permissions / 536870912) % 2) = 0 AND type = 'everyone';

        UPDATE roles SET permissions = permissions + 2199023255552
        WHERE (floor(permissions / 2199023255552) % 2) = 0 AND type = 'everyone';
        """;

    private const string MentionableSql = "UPDATE roles SET mentionable = true;";

    /// <summary>The seven bits the back-fill grants, as the domain names them.</summary>
    private const Permissions ParityBits =
        Permissions.ReadMessageHistory | Permissions.SendVoiceMessages | Permissions.SendPolls |
        Permissions.UseExternalEmojis | Permissions.UseExternalStickers |
        Permissions.UseApplicationCommands | Permissions.UseVoiceActivity;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();
    }
    /// <summary>The @everyone mask a guild created before this work carries, expressed with today's
    /// enum: the old defaults minus everything the parity block added.</summary>
    private static ulong StaleEveryoneMask() =>
        (ulong)(Role.DefaultEveryonePermissions & ~ParityBits);

    // ── Normal case: the stale row ────────────────────────────────────────────

    [Test]
    public async Task Backfill_OnAStaleEveryoneRole_GrantsEveryParityBit()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-stale", StaleEveryoneMask(), RoleType.Everyone);
        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);

        var permissions = await MigrationSqlHarness.ReadPermissionsAsync(connection, "everyone-stale");

        Assert.Multiple(() =>
        {
            Assert.That(permissions.HasFlag(Permissions.ReadMessageHistory), Is.True,
                "without this an existing guild loses its own backlog the day enforcement lands");
            Assert.That(permissions.HasFlag(Permissions.UseApplicationCommands), Is.True);
            Assert.That(permissions.HasFlag(Permissions.UseExternalEmojis), Is.True);
            Assert.That(permissions.HasFlag(Permissions.UseExternalStickers), Is.True);
            Assert.That(permissions.HasFlag(Permissions.UseVoiceActivity), Is.True);
            Assert.That(permissions.HasFlag(Permissions.SendPolls), Is.True);
            Assert.That(permissions.HasFlag(Permissions.SendVoiceMessages), Is.True);
        });
    }

    [Test]
    public async Task Backfill_LandsAStaleRoleOnExactlyTheCurrentDefault()
    {
        // The strongest form of the assertion: an old guild ends up indistinguishable from a new
        // one, which is the property the whole back-fill convention exists to preserve.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-stale", StaleEveryoneMask(), RoleType.Everyone);
        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);

        var permissions = await MigrationSqlHarness.ReadPermissionsAsync(connection, "everyone-stale");

        Assert.That(permissions, Is.EqualTo(Role.DefaultEveryonePermissions));
    }

    [Test]
    public async Task Backfill_GrantsNothingBeyondTheParityBits()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-stale", StaleEveryoneMask(), RoleType.Everyone);
        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);

        var permissions = await MigrationSqlHarness.ReadPermissionsAsync(connection, "everyone-stale");

        Assert.Multiple(() =>
        {
            Assert.That(permissions.HasFlag(Permissions.MentionEveryone), Is.False,
                "deliberately withheld, and a back-fill must not quietly grant it");
            Assert.That(permissions.HasFlag(Permissions.PinMessages), Is.False);
            Assert.That(permissions.HasFlag(Permissions.ManageGuild), Is.False);
            Assert.That(permissions.HasFlag(Permissions.PrioritySpeaker), Is.False);
            Assert.That(permissions.HasFlag(Permissions.CreatePrivateThreads), Is.False);
        });
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Test]
    public async Task Backfill_IsIdempotent()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-stale", StaleEveryoneMask(), RoleType.Everyone);

        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);
        var first = await MigrationSqlHarness.ReadPermissionsAsync(connection, "everyone-stale");

        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);
        var second = await MigrationSqlHarness.ReadPermissionsAsync(connection, "everyone-stale");

        Assert.That(second, Is.EqualTo(first), "the % 2 = 0 guard must make a re-run a no-op");
    }

    [Test]
    public async Task Backfill_OnARoleThatAlreadyHoldsABit_LeavesItAlone()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "everyone-partial",
            StaleEveryoneMask() | (ulong)Permissions.ReadMessageHistory, RoleType.Everyone);

        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);

        var permissions = await MigrationSqlHarness.ReadPermissionsAsync(connection, "everyone-partial");

        Assert.That(permissions, Is.EqualTo(Role.DefaultEveryonePermissions),
            "a bit already set must not be added twice, which would carry into bit 24");
    }

    [Test]
    public async Task Backfill_MakesEveryExistingRoleMentionable()
    {
        // EF gave the new column the CLR default (false) for rows that already existed, which is
        // the opposite of Role.Mentionable's documented default.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-ordinary", 0, RoleType.None);
        await MigrationSqlHarness.ExecuteAsync(connection, MentionableSql);

        Assert.That(await MigrationSqlHarness.ReadMentionableAsync(connection, "role-ordinary"), Is.True);
    }

    // ── Negative case ─────────────────────────────────────────────────────────

    [Test]
    public async Task Backfill_DoesNotTouchOrdinaryRoles()
    {
        // Scoped to type = 'everyone'.
        await using var connection = await MigrationSqlHarness.OpenAsync();

        var ordinary = (ulong)(Permissions.ViewChannel | Permissions.SendMessages);

        await MigrationSqlHarness.SeedRoleAsync(connection, "role-ordinary", ordinary, RoleType.None);
        await MigrationSqlHarness.ExecuteAsync(connection, BackfillSql);

        var permissions = await MigrationSqlHarness.ReadPermissionsAsync(connection, "role-ordinary");

        Assert.That((ulong)permissions, Is.EqualTo(ordinary),
            "an ordinary role must be left exactly as it was");
    }

    // ── The mapping table itself ──────────────────────────────────────────────

    /// <summary>Pins the migration's hard-coded remap table against the enums it describes. The
    /// table cannot be derived from the enums (a migration must not change meaning when the enums
    /// later do), so this is what catches a transposed digit in it.</summary>
    [Test]
    public void RemapMapping_MatchesTheModulePermissionsEnum()
    {
        Assert.Multiple(() =>
        {
            foreach (var (_, newBit, member) in ModulePermissionBitRemap.Mapping)
            {
                var parsed = Enum.Parse<ModulePermissions>(member);
                Assert.That((ulong)parsed, Is.EqualTo(1ul << newBit),
                    $"{member} is at bit {newBit} in the remap table but elsewhere in the enum");
            }

            var mapped = ModulePermissionBitRemap.Mapping.Select(m => m.Member).ToHashSet();
            var declared = Enum.GetNames<ModulePermissions>()
                .Where(n => n != nameof(ModulePermissions.None))
                .ToHashSet();

            Assert.That(declared.Except(mapped), Is.Empty,
                "every module permission must have a row in the remap table");
            Assert.That(mapped.Except(declared), Is.Empty,
                "the remap table must not name a permission that no longer exists");
        });
    }

    [Test]
    public void RemapMapping_UsesEachBitPositionExactlyOnce()
    {
        var oldBits = ModulePermissionBitRemap.Mapping.Select(m => m.OldBit).ToList();
        var newBits = ModulePermissionBitRemap.Mapping.Select(m => m.NewBit).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(oldBits, Is.Unique, "two members cannot have come from the same bit");
            Assert.That(newBits, Is.Unique, "two members cannot land on the same bit");
            Assert.That(newBits, Is.EquivalentTo(Enumerable.Range(0, ModulePermissionBitRemap.Mapping.Length)),
                "the new positions must be contiguous from 0");
        });
    }

    /// <summary>The vacated core bits that the parity block re-uses.</summary>
    [Test]
    public void ReusedCoreBits_AreNoLongerClaimedByAnyModulePermission()
    {
        Permissions[] reused =
        [
            Permissions.ReadMessageHistory, Permissions.SendVoiceMessages, Permissions.SendPolls,
            Permissions.UseExternalEmojis, Permissions.UseExternalStickers,
            Permissions.CreatePrivateThreads, Permissions.UseApplicationCommands,
            Permissions.CreateExpressions, Permissions.ManageExpressions,
            Permissions.PrioritySpeaker, Permissions.RequestToSpeak, Permissions.UseVoiceActivity,
        ];

        var vacated = ModulePermissionBitRemap.Mapping.Select(m => m.OldBit).ToHashSet();

        Assert.Multiple(() =>
        {
            foreach (var permission in reused)
            {
                var bit = (int)Math.Log2((ulong)permission);
                Assert.That(vacated, Does.Contain(bit),
                    $"{permission} sits at bit {bit}, which must be a bit the remap vacates");
            }
        });
    }
}
