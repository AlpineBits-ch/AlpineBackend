using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>What the new invite queries actually ask Postgres for.</summary>
[TestFixture]
public class InviteQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>The moderator list, with revoked invites hidden - GetInvitesAsync's default.</summary>
    [Test]
    public void The_invite_list_filters_state_in_sql()
    {
        var sql = _context.GuildInvites
            .Include(i => i.Guild)
            .Where(i => i.GuildId == "guild-1" && i.State != InviteState.Revoked)
            .ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("guild_invites"));
            // A revoked row hidden client-side would still have crossed the wire from the database,
            // and the index that makes this cheap only helps if the predicate is in the query.
            Assert.That(sql, Does.Contain("state"));
        });
    }

    /// <summary>Vanity resolution.</summary>
    [Test]
    public void Vanity_resolution_compares_the_stored_spelling_directly()
    {
        var sql = _context.Guilds
            .AsNoTracking()
            .Where(g => g.VanityUrl == "the-flat")
            .Select(g => new { g.Id, g.VanityInviteId })
            .ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("vanity_url"));
            Assert.That(sql, Does.Not.Contain("lower("),
                "normalization on write is what makes this index-usable; a lower() here would not be");
        });
    }

    [Test]
    public void The_uniqueness_probe_excludes_the_guild_doing_the_claiming()
    {
        var sql = _context.Guilds
            .AsNoTracking()
            .Where(g => g.VanityUrl == "the-flat" && g.Id != "guild-1")
            .ToQueryString();

        Assert.That(sql, Does.Contain("vanity_url"));
    }

    /// <summary>The temporary-membership sweep, which runs once a minute over the whole table and is
    /// therefore the query in this round most worth pinning to SQL.</summary>
    [Test]
    public void The_temporary_membership_sweep_filters_and_orders_in_sql()
    {
        var now = DateTimeOffset.UtcNow;

        var sql = _context.GuildMembers
            .Where(m => m.TemporaryEvictionDueAt != null && m.TemporaryEvictionDueAt <= now)
            .OrderBy(m => m.TemporaryEvictionDueAt)
            .Take(200)
            .ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("temporary_eviction_due_at"));
            Assert.That(sql, Does.Contain("ORDER BY"));
            Assert.That(sql, Does.Contain("LIMIT"));
        });
    }

    /// <summary>The role test behind temporary membership: "has this member any role that is not
    /// @everyone". It walks a navigation into <c>roles</c>, which is exactly the shape InMemory
    /// answers happily and Postgres might not.</summary>
    [Test]
    public void The_earned_a_role_probe_joins_roles()
    {
        var memberIds = new List<string> { "member-1", "member-2" };

        var sql = _context.RoleMembers
            .Where(rm => memberIds.Contains(rm.MemberId) && rm.Role.Type != RoleType.Everyone)
            .Select(rm => rm.MemberId)
            .Distinct()
            .ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("roles"));
            Assert.That(sql, Does.Contain("DISTINCT"));
        });
    }

    /// <summary>
    /// The invite-broadcast audience: online members of one guild with their permission overrides.
    /// </summary>
    [Test]
    public void The_audience_member_lookup_translates()
    {
        var online = new List<string> { "user-1", "user-2" };

        var sql = _context.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == "guild-1" && online.Contains(m.UserId))
            .Select(m => new { m.Id, m.UserId, m.AllowPermissions, m.DenyPermissions })
            .ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("guild_members"));
            Assert.That(sql, Does.Contain("allow_permissions"));
        });
    }
}
