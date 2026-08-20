using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// SyncFlagAsync against a real Postgres: InMemory evaluates the deny-mask bit test in memory
/// regardless of how it is written, so it cannot catch a predicate EF pushes into SQL as a
/// numeric <c>&amp;</c>, which Postgres has no operator for (42883). This is the reproduction of
/// that production 500.
/// </summary>
[TestFixture]
public class ChannelPrivacyPostgresTests
{
    private const string GuildId = "guild-privacy-pg";
    private const string ChannelId = "chan-privacy-pg";
    private const string EveryoneRoleId = "role-everyone-pg";

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private MicroserviceContext _context = null!;
    private ChannelPrivacyService _privacy = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();

        _context = new PostgresGuildContext();
        _privacy = new ChannelPrivacyService(_context);

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = "user-owner", CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "secret", Description = "d",
            Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await PostgresTestDatabase.ResetAsync();

    /// <summary>
    /// The exact production repro: an @everyone overwrite denying ViewChannel, then
    /// SyncFlagFromOverwriteAsync on that same role. Before the fix this raised
    /// Npgsql.PostgresException 42883 instead of returning.
    /// </summary>
    [Test]
    public async Task SyncFlagFromOverwriteAsync_DoesNotThrow_WhenTheEveryoneRoleDeniesViewChannel()
    {
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-privacy-pg", ChannelId = ChannelId, RoleId = EveryoneRoleId, MemberId = null,
            AllowPermissions = Permissions.None, DenyPermissions = Permissions.ViewChannel,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.DoesNotThrowAsync(async () =>
            await _privacy.SyncFlagFromOverwriteAsync(ChannelId, EveryoneRoleId));
        await _context.SaveChangesAsync();

        var channel = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == ChannelId);
        Assert.That(channel.IsPrivate, Is.True);
    }

    [Test]
    public async Task SyncFlagAsync_ClearsThePrivateFlag_WhenNoEveryoneOverwriteExists()
    {
        var channel = await _context.Channels.FirstAsync(c => c.Id == ChannelId);
        channel.IsPrivate = true;
        await _context.SaveChangesAsync();

        Assert.DoesNotThrowAsync(async () => await _privacy.SyncFlagAsync(ChannelId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == ChannelId);
        Assert.That(reloaded.IsPrivate, Is.False);
    }
}
