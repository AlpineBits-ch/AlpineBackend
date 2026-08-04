using Identity.Infrastructure.Persistence;
using Domain;
using Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Identity.Tests.Migrations;

/// <summary>
/// Rolls two migrations back over data that the migration itself made possible.
///
/// <para><b>Why this needs a real Postgres and cannot be asserted any other way.</b> Both bugs these
/// tests pin are unique-index builds that succeed on an empty table and raise 23505 on a used one.
/// The InMemory provider ignores unique indexes entirely, and reading the <c>Down</c> method is what
/// let both of them through review in the first place - the code looks unremarkable, and only the
/// combination of the index and the rows the <c>Up</c> deliberately started allowing reveals it.</para>
///
/// <para><b>Why it matters more than a normal test.</b> A broken <c>Down</c> is invisible until the
/// day it is needed, and on that day it fails on exactly the databases that have been used - the
/// production ones. §I.6 of the hardening contract promised these migrations were additive and
/// rollback-safe; that promise was false for both of them.</para>
///
/// <para>Each test builds a throwaway database, migrates up to the migration under test, writes the
/// rows the old schema forbade, and migrates back down. Skipped by name when
/// <c>ECHO_TEST_POSTGRES</c> is unset rather than degrading to something that proves nothing.</para>
/// </summary>
[TestFixture]
[Category("Postgres")]
public class MigrationRollbackPostgresTests
{
    /// <summary>An ordinary connection string to any reachable database, e.g.
    /// <c>Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres</c>. It is
    /// only ever used to CREATE and DROP the throwaway database each test runs against.</summary>
    private static string? MaintenanceConnectionString =>
        Environment.GetEnvironmentVariable("ECHO_TEST_POSTGRES");

    private const string ConsolidateDeviceConcepts = "ConsolidateDeviceConcepts";
    private const string AddMlsKeyPackageLifecycle = "AddMlsKeyPackageLifecycle";
    private const string AddMlsIdentityKeys = "AddMlsIdentityKeysBackupAndProtectionLevel";

    private string _database = null!;
    private string _testConnectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        if (string.IsNullOrWhiteSpace(MaintenanceConnectionString))
        {
            Assert.Ignore(
                "Set ECHO_TEST_POSTGRES to a Postgres connection string to run the migration "
                + "rollback tests, e.g. Host=localhost;Port=5433;Database=postgres;Username=postgres;"
                + "Password=postgres. They cannot run against the InMemory provider: that provider "
                + "ignores the unique indexes whose rebuild is the entire failure being pinned.");
        }

        _database = "echo_rollback_" + Guid.NewGuid().ToString("N");
        _testConnectionString = new NpgsqlConnectionStringBuilder(MaintenanceConnectionString)
        {
            Database = _database,
        }.ConnectionString;

        await ExecuteOnMaintenanceAsync($"CREATE DATABASE \"{_database}\";");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (string.IsNullOrWhiteSpace(MaintenanceConnectionString) || _database is null) return;

        NpgsqlConnection.ClearAllPools();
        await ExecuteOnMaintenanceAsync($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE);");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConsolidateDeviceConcepts - client_device_id stopped being globally unique
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The failure: <c>Up</c> replaced a globally unique index on <c>client_device_id</c> with one
    /// scoped to <c>(user_id, client_device_id)</c>, so from that point two accounts may hold the same
    /// client-chosen device id - which is routine, because the value is a per-handset string and
    /// handsets get signed into more than one account. The generated <c>Down</c> rebuilt the old index
    /// as <c>unique</c> and therefore raised 23505 on the last statement of the rollback.
    /// </summary>
    [Test]
    public async Task RollingBackConsolidateDeviceConcepts_WithOneDeviceIdSharedByTwoAccounts_Succeeds()
    {
        await MigrateToAsync(ConsolidateDeviceConcepts);

        // Precisely the shape the Up made legal and the old index forbade.
        await SeedUserAsync("user-a");
        await SeedUserAsync("user-b");
        await SeedDeviceAsync("dev-a", "user-a", "handset-shared");
        await SeedDeviceAsync("dev-b", "user-b", "handset-shared");

        Assert.DoesNotThrowAsync(
            async () => await MigrateToAsync(AddMlsKeyPackageLifecycle),
            "Rolling back ConsolidateDeviceConcepts must not fail on the duplicate client_device_id "
            + "that its own Up started permitting.");

        // Neither user lost their device. Deleting one to satisfy the old index would have destroyed
        // another account's device row - and cascaded to their backups and push tokens.
        Assert.That(await ScalarAsync<long>(
                "SELECT count(*) FROM user_devices WHERE client_device_id = 'handset-shared';"),
            Is.EqualTo(2), "Both accounts must still own their device after the rollback.");

        // The index is back, doing the job the old code used it for - lookups - without the
        // uniqueness that the newer data can no longer satisfy.
        Assert.That(await ScalarAsync<bool>(
                "SELECT indisunique FROM pg_index WHERE indexrelid = "
                + "'ix_user_devices_client_device_id'::regclass;"),
            Is.False, "ix_user_devices_client_device_id must be restored non-unique.");
    }

    /// <summary>The ordinary case still reverts, so the fix above did not buy rollback-safety by
    /// quietly skipping the index.</summary>
    [Test]
    public async Task RollingBackConsolidateDeviceConcepts_WithNoDuplicates_RestoresTheIndex()
    {
        await MigrateToAsync(ConsolidateDeviceConcepts);

        await SeedUserAsync("user-a");
        await SeedDeviceAsync("dev-a", "user-a", "handset-a");

        await MigrateToAsync(AddMlsKeyPackageLifecycle);

        var oldIndex = await ScalarAsync<long>(
            "SELECT count(*) FROM pg_class WHERE relname = 'ix_user_devices_client_device_id';");
        var newIndex = await ScalarAsync<long>(
            "SELECT count(*) FROM pg_class WHERE relname = 'ix_user_devices_user_id_client_device_id';");
        var sessionDeviceColumn = await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'login_sessions' "
            + "AND column_name = 'device_id';");

        Assert.Multiple(() =>
        {
            Assert.That(oldIndex, Is.EqualTo(1), "The old index must exist again after the rollback.");
            Assert.That(newIndex, Is.EqualTo(0), "The per-user index the Up added must be gone.");
            Assert.That(sessionDeviceColumn, Is.EqualTo(0),
                "login_sessions.device_id must be dropped by the rollback.");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AddMlsIdentityKeysBackupAndProtectionLevel - backups became versioned
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The failure: <c>Up</c> turned <c>user_device_backups</c> from one row per device into a
    /// versioned history, then <c>Down</c> rebuilt <c>ix_user_device_backups_device_id</c> as
    /// <c>unique</c> over exactly that history. Any device that had been backed up twice - i.e. every
    /// active device - broke the rollback.
    /// </summary>
    [Test]
    public async Task RollingBackAddMlsIdentityKeys_WithAVersionedBackupHistory_KeepsOnlyTheNewest()
    {
        await MigrateToAsync(AddMlsIdentityKeys);

        await SeedUserAsync("user-a");
        await SeedDeviceAsync("dev-a", "user-a", "handset-a");
        await SeedBackupAsync("blob-v1", "dev-a", "user-a", version: 1);
        await SeedBackupAsync("blob-v3", "dev-a", "user-a", version: 3);
        await SeedBackupAsync("blob-v2", "dev-a", "user-a", version: 2);

        Assert.DoesNotThrowAsync(
            async () => await MigrateToAsync(ConsolidateDeviceConcepts),
            "Rolling back must not fail on the multi-version history its own Up created.");

        var surviving = await ScalarAsync<long>("SELECT count(*) FROM user_device_backups;");
        var survivorId = await ScalarAsync<string>("SELECT id FROM user_device_backups;");
        var indexIsUnique = await ScalarAsync<bool>(
            "SELECT indisunique FROM pg_index WHERE indexrelid = "
            + "'ix_user_device_backups_device_id'::regclass;");

        Assert.Multiple(() =>
        {
            // Narrowing the schema costs the history - that is unavoidable and is why the migration
            // does it explicitly. What must not be lost is the newest blob, which is the only one a
            // restore would ever have used.
            Assert.That(surviving, Is.EqualTo(1),
                "Exactly one blob per device must survive the narrowing.");

            Assert.That(survivorId, Is.EqualTo("blob-v3"),
                "The surviving blob must be the newest version, not whichever row Postgres "
                + "happened to reach first.");

            Assert.That(indexIsUnique, Is.True,
                "The old one-blob-per-device index must be restored, and unique.");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Applies or reverts to <paramref name="target"/>. <see cref="IMigrator"/> rather than
    /// <c>Database.MigrateAsync()</c> because only the former takes a target, which is the whole point
    /// - a rollback is a migration to an earlier name.</summary>
    private async Task MigrateToAsync(string target)
    {
        await using var ctx = NewContext();
        await ctx.Database.GetService<IMigrator>().MigrateAsync(target);
    }

    /// <summary>
    /// The same Npgsql configuration the real context builds for itself, pointed at the throwaway
    /// database.
    ///
    /// <para>Every enum has to be repeated here. <c>MicroserviceContext.OnConfiguring</c> bails out
    /// when the options are already configured - which they must be, to override the connection
    /// string from the environment - so anything it would have done is this method's job, and a
    /// missing mapping shows up as a migration that cannot create its own enum columns.</para>
    /// </summary>
    private MicroserviceContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<MicroserviceContext>();
        builder.UseNpgsql(_testConnectionString, options =>
        {
            options.MapEnum<AgeVertificationLevel>();
            options.MapEnum<Theme>();
            options.MapEnum<DirectMessageSettings>();
            options.MapEnum<PrivacySettings>();
            options.MapEnum<DirectMessagePolicy>();
            options.MapEnum<FriendRequestPolicy>();
            options.MapEnum<Visibility>();
            options.MapEnum<ExplicitContentFilter>();
            options.MapEnum<DeviceStatus>();
            options.MapEnum<DeviceType>();
            options.MapEnum<PushTokenKind>();
            options.MapEnum<UserStatus>();
            options.MapEnum<UserType>();
        }).UseSnakeCaseNamingConvention();

        return new MicroserviceContext(builder.Options);
    }

    /// <summary>Raw SQL rather than the EF model on purpose: the model describes the schema at HEAD,
    /// and every row here is written against an intermediate migration where that shape does not
    /// exist yet.</summary>
    private async Task SeedUserAsync(string userId)
    {
        await ExecuteAsync($"""
            INSERT INTO user_preferences (id, theme, direct_message_settings, data, created_at, updated_at)
            VALUES ('prefs-{userId}', 'system'::theme, 'allow_all'::direct_message_settings, '', now(), now());

            INSERT INTO asp_net_users (id, correlation_id, birth_date, user_preferences_id, created_at, updated_at)
            VALUES ('{userId}', '{userId}-corr', DATE '2000-01-01', 'prefs-{userId}', now(), now());
            """);
    }

    private Task SeedDeviceAsync(string rowId, string userId, string clientDeviceId) =>
        ExecuteAsync($"""
            INSERT INTO user_devices
                (id, user_id, client_device_id, device_name, device_type, identity_public_key, status,
                 created_at, updated_at)
            VALUES
                ('{rowId}', '{userId}', '{clientDeviceId}', 'Test handset', 'mobile'::device_type,
                 '\x00'::bytea, 'active'::device_status, now(), now());
            """);

    private Task SeedBackupAsync(string blobId, string deviceRowId, string userId, int version) =>
        ExecuteAsync($"""
            INSERT INTO user_device_backups (id, user_id, device_id, backup, version, created_at, updated_at)
            VALUES ('{blobId}', '{userId}', '{deviceRowId}', '\x0102'::bytea, {version}, now(), now());
            """);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_testConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_testConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteOnMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
