using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Bots.Tests.Persistence;

/// <summary>
/// Covers the EF model configuration in Bots.Infrastructure.Persistence.MicroserviceContext:
/// basic round-trip persistence plus the two unique indexes the install flow relies on for
/// idempotency (one BotApplication per BotUserId, one BotInstallation per bot+guild pair).
/// </summary>
[TestFixture]
public class MicroserviceContextTests
{
    private string _dbName = null!;
    private TestBotsContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestBotsContext(_dbName);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static BotApplication MakeApplication(string id = "boap_1", string botUserId = "user_bot1") => new()
    {
        Id = id, OwnerUserId = "user_owner", BotUserId = botUserId, Name = "Test Bot",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task BotApplication_RoundTripsThroughSaveChanges()
    {
        _context.BotApplications.Add(MakeApplication());
        await _context.SaveChangesAsync();

        var loaded = await _context.BotApplications.FirstOrDefaultAsync(a => a.Id == "boap_1");

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.BotUserId, Is.EqualTo("user_bot1"));
    }

    [Test]
    public void BotApplication_BotUserId_HasUniqueIndex()
    {
        // The InMemory provider doesn't enforce unique indexes at SaveChanges time the way
        // Postgres does, so assert on the model configuration itself rather than relying on
        // a throw - this is what actually guards install-flow idempotency in production.
        var index = _context.Model.FindEntityType(typeof(BotApplication))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(BotApplication.BotUserId)]));

        Assert.That(index.IsUnique, Is.True);
    }

    [Test]
    public async Task BotInstallation_RoundTripsThroughSaveChanges()
    {
        _context.BotApplications.Add(MakeApplication());
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = "bins_1", BotApplicationId = "boap_1", GuildId = "guild-1",
            InstalledByUserId = "user_installer", GrantedPermissions = 1,
            GuildMemberId = "gmbr_1", InstalledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var loaded = await _context.BotInstallations.FirstOrDefaultAsync(i => i.Id == "bins_1");

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.GuildId, Is.EqualTo("guild-1"));
    }

    [Test]
    public void BotInstallation_ApplicationAndGuildPair_HasUniqueIndex()
    {
        var index = _context.Model.FindEntityType(typeof(BotInstallation))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(BotInstallation.BotApplicationId), nameof(BotInstallation.GuildId)]));

        Assert.That(index.IsUnique, Is.True);
    }

    [Test]
    public async Task BotCommand_RoundTripsThroughSaveChanges()
    {
        _context.BotApplications.Add(MakeApplication());
        _context.BotCommands.Add(new BotCommand
        {
            Id = "boco_1", BotApplicationId = "boap_1", Name = "ping", Description = "Replies with pong",
            OptionsJson = "[]", GuildId = null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var loaded = await _context.BotCommands.FirstOrDefaultAsync(c => c.Id == "boco_1");

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Name, Is.EqualTo("ping"));
    }

    [Test]
    public void BotCommand_HasSeparateUniqueIndexesForGlobalAndGuildScope()
    {
        // Split into two filtered indexes (see MicroserviceContext.OnModelCreating) because
        // Postgres treats every NULL as distinct, so one index across (AppId, GuildId, Name)
        // would never actually enforce uniqueness among global (GuildId == null) commands.
        var indexes = _context.Model.FindEntityType(typeof(BotCommand))!.GetIndexes().ToList();

        var globalIndex = indexes.Single(i => i.Properties.Select(p => p.Name).SequenceEqual(
            [nameof(BotCommand.BotApplicationId), nameof(BotCommand.Name)]));
        var guildIndex = indexes.Single(i => i.Properties.Select(p => p.Name).SequenceEqual(
            [nameof(BotCommand.BotApplicationId), nameof(BotCommand.GuildId), nameof(BotCommand.Name)]));

        Assert.Multiple(() =>
        {
            Assert.That(globalIndex.IsUnique, Is.True);
            Assert.That(guildIndex.IsUnique, Is.True);
        });
    }
}
