using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Asserts that the per-guild DM preference queries compile to SQL against the real Npgsql
/// provider.
///
/// <para>The behavioural suite for these runs on EF InMemory, which cannot fail on an
/// untranslatable query - it evaluates LINQ in memory and reports success for a shape Postgres
/// would 500 on. Same reasoning and same shape as <see cref="InboxQueryTranslationTests"/>; no
/// database is needed, only the provider's SQL generator.</para>
/// </summary>
[TestFixture]
public class GuildPrivacyQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void BuildOverridesQuery_translates()
    {
        var sql = GuildDirectMessagePreferenceService
            .BuildOverridesQuery(_context, "user-1").ToQueryString();

        Assert.That(sql, Does.Contain("guild_direct_message_preferences"));
    }

    [Test]
    public void BuildMembershipQuery_translates_unnarrowed()
    {
        var sql = GuildDirectMessagePreferenceService
            .BuildMembershipQuery(_context, "user-1", []).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildMembershipQuery_translates_with_a_guild_filter()
    {
        // The IN-list branch. A different translation path from the unnarrowed one, and the one a
        // caller passing GuildIds actually runs.
        var sql = GuildDirectMessagePreferenceService
            .BuildMembershipQuery(_context, "user-1", ["gild-1", "gild-2"]).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildSharedMembershipQuery_translates()
    {
        // Two IN-lists and a DISTINCT over a projected struct - the shape most likely to fall out
        // of translation, and the one InMemory would happily evaluate in memory instead.
        var sql = GetSharedGuildsHandler
            .BuildSharedMembershipQuery(_context, ["user-a", "user-b"], ["gild-1", "gild-2"])
            .ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("SELECT"));
            Assert.That(sql, Does.Contain("DISTINCT"));
        });
    }

    [Test]
    public void BuildSharedMembershipQuery_translates_for_a_single_pair()
    {
        var sql = GetSharedGuildsHandler
            .BuildSharedMembershipQuery(_context, ["user-a"], ["gild-1"]).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void The_unique_pair_index_exists_on_the_model()
    {
        // The endpoint and the bus handler both upsert on (UserId, GuildId); a duplicate would make
        // "may this person DM me" a coin flip.
        var entity = _context.Model.FindEntityType(typeof(Guild.Domain.Entity.GuildDirectMessagePreference))!;

        var unique = entity.GetIndexes().Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        Assert.That(unique, Does.Contain("UserId,GuildId"));
    }
}
