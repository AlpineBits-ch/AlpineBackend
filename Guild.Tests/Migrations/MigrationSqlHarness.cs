using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Guild.Tests.Migrations;

/// <summary>
/// Shared plumbing for the two fixtures that execute migration SQL against a real Postgres.
/// </summary>
internal static class MigrationSqlHarness
{
    public const string GuildId = "guild-migration";

    public static async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(Env.Database.ConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Creates the guild the seeded roles hang off, through the real model.</summary>
    public static async Task SeedGuildAsync(string guildId = GuildId)
    {
        await using var context = new PostgresGuildContext();

        if (await context.Guilds.AnyAsync(g => g.Id == guildId)) return;

        context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = guildId,
            Name = "migration-guild",
            OwnerId = "owner",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Creates a guild member, through the real model: it has a dozen columns these tests
    /// do not care about and hand-listing them would break the first time one is added.</summary>
    public static async Task SeedMemberAsync(string id, string guildId = GuildId)
    {
        await using var context = new PostgresGuildContext();

        context.GuildMembers.Add(new GuildMember
        {
            Id = id,
            GuildId = guildId,
            UserId = $"user-{id}",
            JoinedAt = DateTime.UtcNow,
            SearchValue = $"user-{id}#{guildId}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Creates a channel, through the real model, for the same reason as the member.</summary>
    public static async Task SeedChannelAsync(string id, string guildId = GuildId)
    {
        await using var context = new PostgresGuildContext();

        context.Channels.Add(new Guild.Domain.Aggregates.Channel
        {
            Id = id,
            GuildId = guildId,
            Name = "chat",
            Description = "d",
            Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Inserts a read state, bypassing the model so a pair can be stacked.</summary>
    public static async Task SeedReadStateAsync(
        NpgsqlConnection connection,
        string id,
        string memberId,
        string channelId,
        DateTimeOffset? lastReadAt,
        DateTimeOffset? updatedAt = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO read_states (id, member_id, channel_id, last_read_message_id, last_read_at,
                                     message_count_at_read, created_at, updated_at)
            VALUES (@id, @member_id, @channel_id, @last_read_message_id, @last_read_at,
                    0, @updated_at, @updated_at);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("member_id", memberId);
        command.Parameters.AddWithValue("channel_id", channelId);
        command.Parameters.AddWithValue("last_read_message_id", (object?)(lastReadAt is null ? null : $"mesg-{id}") ?? DBNull.Value);
        command.Parameters.AddWithValue("last_read_at", (object?)lastReadAt ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", updatedAt ?? lastReadAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The ids still present in <c>read_states</c>.</summary>
    public static async Task<List<string>> ReadReadStateIdsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM read_states ORDER BY id;";

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>Inserts a role carrying an arbitrary raw mask, bypassing the model.</summary>
    public static async Task SeedRoleAsync(
        NpgsqlConnection connection,
        string id,
        ulong permissions,
        RoleType type = RoleType.None,
        bool mentionable = false,
        string guildId = GuildId,
        DateTimeOffset? createdAt = null)
    {
        var typeLiteral = type == RoleType.Everyone ? "everyone" : "none";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO roles (id, guild_id, name, color, position, type, permissions, module_permissions,
                               hoist, mentionable, is_managed, created_at, updated_at)
            VALUES (@id, @guild_id, 'r', '#000000', 0, '{typeLiteral}', @permissions, 0,
                    false, @mentionable, false, @created_at, @created_at);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("guild_id", guildId);
        command.Parameters.AddWithValue("permissions", (decimal)permissions);
        command.Parameters.AddWithValue("mentionable", mentionable);
        command.Parameters.AddWithValue("created_at", createdAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts a role membership.</summary>
    public static async Task SeedRoleMemberAsync(
        NpgsqlConnection connection,
        string id,
        string roleId,
        string memberId,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? createdAt = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO role_members (id, role_id, member_id, expires_at, created_at, updated_at)
            VALUES (@id, @role_id, @member_id, @expires_at, @created_at, @created_at);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("role_id", roleId);
        command.Parameters.AddWithValue("member_id", memberId);
        command.Parameters.AddWithValue("expires_at", (object?)expiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", createdAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The ids still present in <c>role_members</c>, oldest first.</summary>
    public static async Task<List<string>> ReadRoleMemberIdsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM role_members ORDER BY created_at, id;";

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    public static async Task<RoleType> ReadRoleTypeAsync(NpgsqlConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type::text FROM roles WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);

        var literal = (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"role {id} was not seeded");

        return literal == "everyone" ? RoleType.Everyone : RoleType.None;
    }

    public static async Task<(decimal Core, decimal Module)> ReadRoleMasksAsync(
        NpgsqlConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT permissions, module_permissions FROM roles WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException($"role {id} was not seeded");
        return (reader.GetDecimal(0), reader.GetDecimal(1));
    }

    public static async Task<Permissions> ReadPermissionsAsync(NpgsqlConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT permissions FROM roles WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        return (Permissions)(ulong)(decimal)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<bool> ReadMentionableAsync(NpgsqlConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT mentionable FROM roles WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
