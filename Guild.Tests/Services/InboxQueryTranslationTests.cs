using Guild.Application.Services;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Asserts that the inbox queries compile to SQL against the real Npgsql provider.
/// </summary>
[TestFixture]
public class InboxQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void BuildUnreadQuery_translates_without_a_cursor()
    {
        // The shape /inbox/summary runs. Both it and /inbox/unread 500'd on this.
        var sql = InboxService.BuildUnreadQuery(_context, "user-1", cursor: null).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildUnreadQuery_translates_with_a_cursor()
    {
        // The keyset branch adds a Where over the projected row - a different translation path.
        var cursor = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow.UtcTicks}:chan-1"));

        var sql = InboxService.BuildUnreadQuery(_context, "user-1", cursor).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>The ORDER BY must land on the source columns, not on the select list.</summary>
    [Test]
    public void BuildUnreadQuery_orders_on_the_channel_columns()
    {
        var sql = InboxService.BuildUnreadQuery(_context, "user-1", cursor: null).ToQueryString();

        Assert.That(sql, Does.Contain("ORDER BY c.last_activity_at DESC, c.id"));
    }

    /// <summary>The left join is the whole point of the unread predicate - a channel nobody has
    /// opened has no read state, and an inner join would drop exactly those.</summary>
    [Test]
    public void BuildUnreadQuery_keeps_the_read_state_join_outer()
    {
        var sql = InboxService.BuildUnreadQuery(_context, "user-1", cursor: null).ToQueryString();

        Assert.That(sql, Does.Contain("LEFT JOIN read_states"));
    }

    [Test]
    public void BuildBroadcastQuery_translates_without_a_cursor()
    {
        var sql = InboxMentionService.BuildBroadcastQuery(
            _context, "user-1", ["gild-1"], DateTimeOffset.UtcNow.AddDays(-7),
            beforeAt: null, beforeId: null).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildBroadcastQuery_translates_with_a_cursor()
    {
        // string.Compare in the keyset tie-break is the part most likely to stop translating.
        var sql = InboxMentionService.BuildBroadcastQuery(
            _context, "user-1", ["gild-1"], DateTimeOffset.UtcNow.AddDays(-7),
            DateTimeOffset.UtcNow, "mesg-1").ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    // ── Waiting-on-you ───────────────────────────────────────────────────────

    [Test]
    public void BuildChoreQuery_translates()
    {
        var sql = InboxTaskService
            .BuildChoreQuery(_context, "user-1", DateTimeOffset.UtcNow.AddDays(1))
            .ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>The correlated <c>!Votes.Any(...)</c> is the part most likely to stop
    /// translating.</summary>
    [Test]
    public void BuildDecisionQuery_translates()
    {
        var sql = InboxTaskService
            .BuildDecisionQuery(_context, "user-1", DateTimeOffset.UtcNow)
            .ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildAssignmentQuery_translates()
    {
        var sql = InboxTaskService.BuildAssignmentQuery(_context, "user-1").ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>Two correlated subqueries over the same collection - a named share, or an Equal
    /// split that named nobody - which is the part most likely to stop translating.</summary>
    [Test]
    public void BuildBillQuery_translates()
    {
        var sql = InboxTaskService
            .BuildBillQuery(_context, "user-1", DateTimeOffset.UtcNow.AddDays(5))
            .ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>The outer join onto recipes is the shape at risk here: an inner one would compile
    /// perfectly and silently drop every free-text entry.</summary>
    [Test]
    public void BuildMealQuery_translates()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var sql = InboxTaskService
            .BuildMealQuery(_context, "user-1", today, today.AddDays(1))
            .ToQueryString();

        Assert.That(sql, Does.Contain("LEFT JOIN"));
    }

    [Test]
    public void BuildMaintenanceQuery_translates()
    {
        var sql = InboxTaskService
            .BuildMaintenanceQuery(_context, "user-1", DateTimeOffset.UtcNow)
            .ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }
}
