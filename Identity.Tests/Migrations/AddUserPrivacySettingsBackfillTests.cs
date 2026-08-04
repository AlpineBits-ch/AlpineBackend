using Domain;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Identity.Tests.Migrations;

/// <summary>
/// The <c>AddUserPrivacySettings</c> backfill, run against a real Postgres over real legacy rows.
///
/// <para><b>Why this cannot be asserted any other way.</b> The backfill is hand-written SQL inside
/// the migration - the generator cannot infer a data move - and it is executed exactly once, on
/// databases that already hold every user the product has. There is no code path that runs it again
/// afterwards, so a mistranslation is discovered as "everyone's DM setting is wrong" rather than as
/// a failing request. The InMemory provider does not run migrations at all, and the enum-to-enum
/// translation is expressed in Postgres <c>CASE</c> over Postgres enum types, so nothing short of a
/// real database exercises it.</para>
///
/// <para>Skipped by name when <c>ECHO_TEST_POSTGRES</c> is unset, matching
/// <see cref="MigrationRollbackPostgresTests"/>.</para>
/// </summary>
[TestFixture]
[Category("Postgres")]
public class AddUserPrivacySettingsBackfillTests
{
    private const string PreviousMigration = "AddUserOnboarding";
    private const string MigrationUnderTest = "AddUserPrivacySettings";

    private static string? MaintenanceConnectionString =>
        Environment.GetEnvironmentVariable("ECHO_TEST_POSTGRES");

    private string _database = null!;
    private string _testConnectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        if (string.IsNullOrWhiteSpace(MaintenanceConnectionString))
        {
            Assert.Ignore(
                "Set ECHO_TEST_POSTGRES to a Postgres connection string to run the privacy-settings "
                + "backfill test, e.g. Host=localhost;Port=5433;Database=postgres;Username=postgres;"
                + "Password=postgres.");
        }

        _database = "echo_backfill_" + Guid.NewGuid().ToString("N");
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

    /// <summary>
    /// All three legacy consent flags and all three legacy DM settings, translated in one pass.
    ///
    /// <para>The DM mapping is the one worth staring at: the old names describe the <i>filter</i>
    /// (<c>FilterNonFriends</c>) and the new ones describe the <i>people admitted</i>
    /// (<c>Friends</c>), so the two enums read as opposites while meaning the same thing. Getting
    /// <c>FilterAll</c> and <c>AllowAll</c> the wrong way round would silently open every account
    /// that had locked itself down.</para>
    /// </summary>
    [Test]
    public async Task Backfill_TranslatesEveryLegacyValue()
    {
        await MigrateToAsync(PreviousMigration);

        await SeedUserAsync("user-collect", "allow_all", "allow_data_collection");
        await SeedUserAsync("user-clips", "filter_non_friends", "allow_voice_recorded_in_clips");
        await SeedUserAsync("user-personal", "filter_all", "allow_data_use_for_personalization");
        await SeedUserAsync("user-none", "filter_non_friends", "none");

        await MigrateToAsync(MigrationUnderTest);

        Assert.Multiple(async () =>
        {
            // PrivacySettings.AllowDataCollection -> allow_data_collection, and nothing else.
            Assert.That(await FlagsOf("user-collect"), Is.EqualTo((true, false, false)));
            Assert.That(await FlagsOf("user-clips"), Is.EqualTo((false, false, true)));
            Assert.That(await FlagsOf("user-personal"), Is.EqualTo((false, true, false)));
            Assert.That(await FlagsOf("user-none"), Is.EqualTo((false, false, false)));

            Assert.That(await PolicyOf("user-collect"), Is.EqualTo("everyone"), "AllowAll -> Everyone");
            Assert.That(await PolicyOf("user-clips"), Is.EqualTo("friends"), "FilterNonFriends -> Friends");
            Assert.That(await PolicyOf("user-personal"), Is.EqualTo("nobody"), "FilterAll -> Nobody");
        });
    }

    [Test]
    public async Task Backfill_GivesEveryExistingAccountExactlyOneRow()
    {
        await MigrateToAsync(PreviousMigration);

        await SeedUserAsync("user-a", "allow_all", "none");
        await SeedUserAsync("user-b", "filter_all", "none");
        await SeedUserAsync("user-c", "filter_non_friends", "none");

        await MigrateToAsync(MigrationUnderTest);

        Assert.That(await ScalarAsync<long>("SELECT count(*) FROM user_privacy_settings;"), Is.EqualTo(3));
        Assert.That(await ScalarAsync<long>(
                "SELECT count(DISTINCT user_id) FROM user_privacy_settings;"),
            Is.EqualTo(3), "the unique index on user_id would have failed the migration otherwise");
    }

    /// <summary>
    /// Every field the legacy row could not express takes the entity's default - the same values a
    /// freshly registered account gets. The consent-adjacent ones are the point: an existing account
    /// must not be silently made discoverable by email, or have its birthday exposed, by a migration
    /// it never asked for.
    /// </summary>
    [Test]
    public async Task Backfill_AppliesTheDocumentedDefaultsForEverythingElse()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedUserAsync("user-a", "allow_all", "allow_data_collection");
        await MigrateToAsync(MigrationUnderTest);

        Assert.Multiple(async () =>
        {
            Assert.That(await ScalarAsync<string>(
                "SELECT friend_request_policy::text FROM user_privacy_settings;"), Is.EqualTo("everyone"));
            Assert.That(await ScalarAsync<bool>(
                "SELECT discoverable_by_username FROM user_privacy_settings;"), Is.True);
            Assert.That(await ScalarAsync<bool>(
                "SELECT discoverable_by_email FROM user_privacy_settings;"), Is.False);
            Assert.That(await ScalarAsync<bool>(
                "SELECT discoverable_by_phone FROM user_privacy_settings;"), Is.False);
            Assert.That(await ScalarAsync<string>(
                "SELECT birthday_visibility::text FROM user_privacy_settings;"), Is.EqualTo("nobody"));
            Assert.That(await ScalarAsync<string>(
                "SELECT mutual_friends_visibility::text FROM user_privacy_settings;"), Is.EqualTo("friends"));
            Assert.That(await ScalarAsync<string>(
                "SELECT explicit_content_filter::text FROM user_privacy_settings;"),
                Is.EqualTo("unknown_senders"));
            Assert.That(await ScalarAsync<long>(
                "SELECT count(*) FROM user_privacy_settings WHERE dm_retention_days IS NULL;"),
                Is.EqualTo(1), "null is 'keep forever'; retention is opt-in");
            Assert.That(await ScalarAsync<int>("SELECT version FROM user_privacy_settings;"), Is.Zero);
        });
    }

    [Test]
    public async Task Backfill_MintsWellFormedPrefixedIds()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedUserAsync("user-a", "allow_all", "none");
        await MigrateToAsync(MigrationUnderTest);

        var id = await ScalarAsync<string>("SELECT id FROM user_privacy_settings;");

        Assert.Multiple(() =>
        {
            Assert.That(id, Does.StartWith("upvs_"));
            Assert.That(id, Has.Length.EqualTo("upvs_".Length + 26),
                "Ids.Identifier mints prefix + a 26-character body; a backfilled row that does not "
                + "match is an id no client-side parser will recognise");
            Assert.That(id["upvs_".Length..], Does.Match("^[0-9A-HJKMNP-TV-Z]{26}$"),
                "the body must be Crockford base32, which excludes I, L, O and U");
        });
    }

    /// <summary>
    /// The legacy columns are copied, not moved. Shipped clients still read them off
    /// <c>GET /users/self</c>, and a migration that emptied them would break every one of them on
    /// deploy - which is the exact failure the additive rule exists to prevent.
    /// </summary>
    [Test]
    public async Task Backfill_LeavesTheLegacyColumnsIntact()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedUserAsync("user-a", "filter_all", "allow_data_collection");

        await MigrateToAsync(MigrationUnderTest);

        Assert.Multiple(async () =>
        {
            Assert.That(await ScalarAsync<string>(
                "SELECT direct_message_settings::text FROM user_preferences WHERE id = 'prefs-user-a';"),
                Is.EqualTo("filter_all"));
            Assert.That(await ScalarAsync<string>(
                "SELECT privacy_settings::text FROM user_preferences WHERE id = 'prefs-user-a';"),
                Is.EqualTo("allow_data_collection"));
        });
    }

    /// <summary>
    /// No account is skipped, whatever it looks like.
    ///
    /// <para>The backfill drives off <c>asp_net_users</c> and joins preferences in, rather than the
    /// other way round, because an account that ends up with <i>no</i> settings row is one every
    /// consuming service resolves through its fail-closed path forever - silently, and only for that
    /// account. <c>user_preferences_id</c> is NOT NULL today, so the LEFT JOIN is belt-and-braces
    /// rather than a live case; this test pins the invariant it protects.</para>
    /// </summary>
    [Test]
    public async Task Backfill_LeavesNoAccountWithoutARow()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedUserAsync("user-a", "allow_all", "allow_data_collection");
        await SeedUserAsync("user-b", "filter_all", "none");

        await MigrateToAsync(MigrationUnderTest);

        Assert.That(await ScalarAsync<long>("""
            SELECT count(*) FROM asp_net_users u
            WHERE NOT EXISTS (SELECT 1 FROM user_privacy_settings s WHERE s.user_id = u.id);
            """), Is.Zero);
    }

    /// <summary>The rollback drops the table. Asserted because a <c>Down</c> nobody ran is a
    /// <c>Down</c> that fails on the day it is needed - see
    /// <see cref="MigrationRollbackPostgresTests"/> for the two occasions that already happened.</summary>
    [Test]
    public async Task RollingBack_DropsTheTableAndLeavesTheLegacyColumnsUsable()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedUserAsync("user-a", "allow_all", "allow_data_collection");
        await MigrateToAsync(MigrationUnderTest);

        Assert.DoesNotThrowAsync(async () => await MigrateToAsync(PreviousMigration));

        Assert.That(await ScalarAsync<long>(
                "SELECT count(*) FROM information_schema.tables WHERE table_name = 'user_privacy_settings';"),
            Is.Zero);
        Assert.That(await ScalarAsync<string>(
                "SELECT direct_message_settings::text FROM user_preferences WHERE id = 'prefs-user-a';"),
            Is.EqualTo("allow_all"), "the legacy column is what a reverted deployment falls back on");
    }

    // ══════════════════════════════════════════════════════════════════════════

    private async Task<(bool Collection, bool Personalization, bool Clips)> FlagsOf(string userId)
    {
        var collection = await ScalarAsync<bool>(
            $"SELECT allow_data_collection FROM user_privacy_settings WHERE user_id = '{userId}';");
        var personalization = await ScalarAsync<bool>(
            $"SELECT allow_personalization FROM user_privacy_settings WHERE user_id = '{userId}';");
        var clips = await ScalarAsync<bool>(
            $"SELECT allow_voice_recording_in_clips FROM user_privacy_settings WHERE user_id = '{userId}';");
        return (collection, personalization, clips);
    }

    private Task<string> PolicyOf(string userId) => ScalarAsync<string>(
        $"SELECT direct_message_policy::text FROM user_privacy_settings WHERE user_id = '{userId}';");

    private async Task MigrateToAsync(string target)
    {
        await using var ctx = NewContext();
        await ctx.Database.GetService<IMigrator>().MigrateAsync(target);
    }

    /// <summary>The same Npgsql configuration the real context builds for itself, pointed at the
    /// throwaway database. Every enum has to be repeated - <c>OnConfiguring</c> bails out when the
    /// options are already configured, and a missing mapping shows up as a migration that cannot
    /// create its own enum columns.</summary>
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

    /// <summary>Raw SQL rather than the EF model: every row here is written against the schema as it
    /// stood <i>before</i> the migration under test, which is a shape the model no longer
    /// describes.</summary>
    private async Task SeedUserAsync(string userId, string legacyDm, string legacyPrivacy)
    {
        await ExecuteAsync($"""
            INSERT INTO user_preferences (id, theme, direct_message_settings, privacy_settings, data, created_at, updated_at)
            VALUES ('prefs-{userId}', 'system'::theme, '{legacyDm}'::direct_message_settings,
                    '{legacyPrivacy}'::privacy_settings, '', now(), now());

            INSERT INTO asp_net_users (id, correlation_id, birth_date, user_preferences_id, created_at, updated_at)
            VALUES ('{userId}', '{userId}-corr', DATE '2000-01-01', 'prefs-{userId}', now(), now());
            """);
    }

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
