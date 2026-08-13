using Isle.Api.Services.State;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Progression;

/// <summary>Playtime, and specifically the ways it goes wrong.</summary>
[TestFixture]
public class PlaySessionTrackerTests
{
    private TestIsleContext _context = null!;
    private PlaySessionTracker _tracker = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _tracker = new PlaySessionTracker(_context, NullLogger<PlaySessionTracker>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<string> AddPlayerAsync(string steamId = "steam-1")
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player.Id;
    }

    /// <summary>Writes a session directly, so a test can arrange a state the tracker would take hours
    /// of real time to reach.</summary>
    private async Task<PlaySession> ArrangeOpenSessionAsync(
        string playerId, DateTimeOffset startedAt, DateTimeOffset lastSeenAt, string? species = null)
    {
        var session = PlaySession.Open(playerId, species, startedAt);
        session.LastSeenAt = lastSeenAt;
        _context.PlaySessions.Add(session);
        await _context.SaveChangesAsync();
        return session;
    }

    // ── Normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task Start_OpensASession()
    {
        var playerId = await AddPlayerAsync();

        await _tracker.StartAsync(playerId, "Ceratosaurus");

        var session = await _context.PlaySessions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(session.PlayerId, Is.EqualTo(playerId));
            Assert.That(session.IsOpen, Is.True);
            Assert.That(session.Species, Is.EqualTo("Ceratosaurus"));
        });
    }

    [Test]
    public async Task Leave_ClosesTheSessionAndSettlesItsLength()
    {
        var playerId = await AddPlayerAsync();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-90);
        await ArrangeOpenSessionAsync(playerId, startedAt, startedAt);

        await _tracker.EndAsync(playerId);

        var session = await _context.PlaySessions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(session.IsOpen, Is.False);
            Assert.That(session.EndReason, Is.EqualTo(PlaySessionEndReason.Left));

            // A leave event is itself a confirmation the player was there until now, so the whole 90
            // minutes counts - it is not truncated back to a heartbeat that predates it.
            Assert.That(session.DurationSeconds, Is.EqualTo(90 * 60).Within(5));
        });
    }

    [Test]
    public async Task Summarise_AddsUpSettledAndOpenTime()
    {
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;

        await ArrangeOpenSessionAsync(playerId, now.AddHours(-3), now.AddHours(-3));
        await _tracker.EndAsync(playerId);

        await ArrangeOpenSessionAsync(playerId, now.AddMinutes(-30), now.AddMinutes(-1));

        var summary = await _tracker.SummariseAsync(playerId);

        // The open session contributes to its heartbeat (29 minutes), not to now.
        Assert.That(summary.TotalSeconds, Is.EqualTo(3 * 3600 + 29 * 60).Within(10));
    }

    // ── The missed leave event ────────────────────────────────────────────

    [Test]
    public async Task Reconcile_APlayerMissingFromAFreshRosterIsClosedAtTheirLastHeartbeat()
    {
        // The crash case. Nothing reported the leave, so without this the session stays open forever.
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        await ArrangeOpenSessionAsync(playerId, now.AddMinutes(-40), now.AddMinutes(-10));

        var closed = await _tracker.ReconcileAsync([]);

        var session = await _context.PlaySessions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.EqualTo(1));
            Assert.That(session.EndReason, Is.EqualTo(PlaySessionEndReason.Disconnected));

            // 30 minutes - the time up to the last confirmation, not the 40 up to now.
            Assert.That(session.DurationSeconds, Is.EqualTo(30 * 60).Within(5));
        });
    }

    [Test]
    public async Task Reconcile_ASessionPastTheHardCapIsClosedEvenWithNoRosterAtAll()
    {
        // The outage case: this service was down, or RCON was, for longer than the window.
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var lastSeen = now - PlaySession.AbandonedAfter - TimeSpan.FromMinutes(5);
        await ArrangeOpenSessionAsync(playerId, lastSeen.AddMinutes(-20), lastSeen);

        var closed = await _tracker.ReconcileAsync(online: null);

        var session = await _context.PlaySessions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.EqualTo(1));
            Assert.That(session.EndReason, Is.EqualTo(PlaySessionEndReason.Abandoned));
            Assert.That(session.DurationSeconds, Is.EqualTo(20 * 60).Within(5));
        });
    }

    [Test]
    public async Task Reconcile_WithNoRosterASessionInsideTheWindowIsLeftAlone()
    {
        // The regression this guards: a dropped RCON read looks exactly like an empty server, and
        // treating it as one would end everybody's session mid-evening.
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        await ArrangeOpenSessionAsync(playerId, now.AddMinutes(-20), now.AddMinutes(-1));

        var closed = await _tracker.ReconcileAsync(online: null);

        Assert.That(closed, Is.EqualTo(0));
        Assert.That((await _context.PlaySessions.SingleAsync()).IsOpen, Is.True);
    }

    [Test]
    public async Task Summarise_AnAbandonedSessionStopsGrowingBetweenReads()
    {
        // The property the whole design exists for.
        var playerId = await AddPlayerAsync();
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-3);
        await ArrangeOpenSessionAsync(playerId, lastSeen.AddHours(-1), lastSeen);

        var before = await _tracker.SummariseAsync(playerId);
        await _tracker.ReconcileAsync(online: null);
        var after = await _tracker.SummariseAsync(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(before.TotalSeconds, Is.EqualTo(3600).Within(5));
            Assert.That(after.TotalSeconds, Is.EqualTo(before.TotalSeconds));
        });
    }

    [Test]
    public async Task Start_AfterACrashSettlesTheAbandonedSessionInsteadOfExtendingIt()
    {
        var playerId = await AddPlayerAsync();
        var lastSeen = DateTimeOffset.UtcNow - PlaySession.AbandonedAfter - TimeSpan.FromMinutes(1);
        await ArrangeOpenSessionAsync(playerId, lastSeen.AddMinutes(-45), lastSeen);

        await _tracker.StartAsync(playerId, "Dryosaurus");

        var sessions = await _context.PlaySessions.OrderBy(session => session.StartedAt).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sessions, Has.Count.EqualTo(2));
            Assert.That(sessions[0].EndReason, Is.EqualTo(PlaySessionEndReason.Abandoned));
            Assert.That(sessions[0].DurationSeconds, Is.EqualTo(45 * 60).Within(5));
            Assert.That(sessions[1].IsOpen, Is.True);
        });
    }

    // ── Edge ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Start_IsIdempotent()
    {
        // A redelivered join must not double-count the evening.
        var playerId = await AddPlayerAsync();

        await _tracker.StartAsync(playerId);
        await _tracker.StartAsync(playerId);

        Assert.That(await _context.PlaySessions.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Reconcile_ASpeciesChangeSplitsTheSessionWithoutLosingTime()
    {
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        await ArrangeOpenSessionAsync(playerId, now.AddMinutes(-60), now.AddMinutes(-1), "Ceratosaurus");

        await _tracker.ReconcileAsync([new OnlinePlayer(playerId, "Dryosaurus")]);

        var sessions = await _context.PlaySessions.OrderBy(session => session.StartedAt).ToListAsync();
        var summary = await _tracker.SummariseAsync(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(sessions, Has.Count.EqualTo(2));
            Assert.That(sessions[0].EndReason, Is.EqualTo(PlaySessionEndReason.SpeciesChange));
            Assert.That(sessions[0].Species, Is.EqualTo("Ceratosaurus"));
            Assert.That(sessions[1].Species, Is.EqualTo("Dryosaurus"));

            // The split is adjacent, so the hour is still an hour.
            Assert.That(summary.TotalSeconds, Is.EqualTo(60 * 60).Within(5));
        });
    }

    [Test]
    public async Task Reconcile_ARosterEntryWithNoSpeciesDoesNotSplitTheSession()
    {
        // The roster answers without a class for a player mid-respawn.
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        await ArrangeOpenSessionAsync(playerId, now.AddMinutes(-30), now.AddMinutes(-1), "Ceratosaurus");

        await _tracker.ReconcileAsync([new OnlinePlayer(playerId, null)]);

        var session = await _context.PlaySessions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(session.IsOpen, Is.True);
            Assert.That(session.Species, Is.EqualTo("Ceratosaurus"));
        });
    }

    [Test]
    public async Task Reconcile_OpensASessionForAnOnlinePlayerWhoHasNone()
    {
        // The dropped-join case; without this their whole evening is never counted.
        var playerId = await AddPlayerAsync();

        await _tracker.ReconcileAsync([new OnlinePlayer(playerId, "Stegosaurus")]);

        var session = await _context.PlaySessions.SingleAsync();
        Assert.That(session.IsOpen, Is.True);
        Assert.That(session.Species, Is.EqualTo("Stegosaurus"));
    }

    [Test]
    public async Task FavouriteSpecies_IsTheOneWithTheMostTimeBehindIt()
    {
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;

        await CloseSessionAsync(playerId, "Dryosaurus", now.AddHours(-10), TimeSpan.FromHours(1));
        await CloseSessionAsync(playerId, "Ceratosaurus", now.AddHours(-8), TimeSpan.FromHours(4));
        await CloseSessionAsync(playerId, "Dryosaurus", now.AddHours(-3), TimeSpan.FromHours(2));

        var summary = await _tracker.SummariseAsync(playerId);

        Assert.Multiple(() =>
        {
            // Ceratosaurus wins on a single sitting; Dryosaurus has more sessions but less time.
            Assert.That(summary.FavouriteSpecies, Is.EqualTo("Ceratosaurus"));
            Assert.That(summary.TotalSeconds, Is.EqualTo(7 * 3600).Within(5));
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────

    [Test]
    public async Task Summarise_APlayerWhoHasNeverPlayedIsZeroRatherThanAnError()
    {
        var playerId = await AddPlayerAsync();

        var summary = await _tracker.SummariseAsync(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalSeconds, Is.Zero);
            Assert.That(summary.FavouriteSpecies, Is.Null);
            Assert.That(summary.FirstPlayedAt, Is.Null);
        });
    }

    [Test]
    public async Task Leave_WithNoOpenSessionIsANoOp()
    {
        // A leave for a player who never registered as joining.
        var playerId = await AddPlayerAsync();

        await _tracker.EndAsync(playerId);

        Assert.That(await _context.PlaySessions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Leave_DoesNotReopenOrRetimeAnAlreadyClosedSession()
    {
        var playerId = await AddPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        await ArrangeOpenSessionAsync(playerId, now.AddMinutes(-40), now.AddMinutes(-10));

        await _tracker.ReconcileAsync([]);
        var settled = (await _context.PlaySessions.AsNoTracking().SingleAsync()).DurationSeconds;

        // The leave event turns up a moment after the reconcile pass beat it to the punch.
        await _tracker.EndAsync(playerId);

        var session = await _context.PlaySessions.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(session.DurationSeconds, Is.EqualTo(settled));
            Assert.That(session.EndReason, Is.EqualTo(PlaySessionEndReason.Disconnected));
        });
    }

    [Test]
    public async Task Start_WithABlankPlayerIdWritesNothing()
    {
        await _tracker.StartAsync("   ");

        Assert.That(await _context.PlaySessions.CountAsync(), Is.Zero);
    }

    private async Task CloseSessionAsync(string playerId, string species, DateTimeOffset startedAt, TimeSpan length)
    {
        var session = PlaySession.Open(playerId, species, startedAt);
        session.LastSeenAt = startedAt + length;
        session.Close(PlaySessionEndReason.Left, startedAt + length);
        _context.PlaySessions.Add(session);
        await _context.SaveChangesAsync();
    }
}
