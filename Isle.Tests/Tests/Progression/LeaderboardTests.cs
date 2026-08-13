using Isle.Api.Endpoints;
using Isle.Api.Services.Progression;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Progression;

/// <summary>
/// The board, its ranking, and the opt-out that has to remove a player from the listing without
/// removing them from their own rank.
/// </summary>
[TestFixture]
public class LeaderboardTests
{
    private TestIsleContext _context = null!;
    private PlaySessionTracker _sessions = null!;
    private LeaderboardService _service = null!;
    private PlayerPreferencesService _preferences = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _sessions = new PlaySessionTracker(_context, NullLogger<PlaySessionTracker>.Instance);
        _service = new LeaderboardService(_context, _sessions);
        _preferences = new PlayerPreferencesService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Player> AddPlayerAsync(string steamId, string name, long xp, string? userId = null)
    {
        var player = TestData.Player(steamId, name);
        player.UserId = userId;

        // Player.Create seeds a starting balance, so drive the score from a known base.
        player.TrySpendXp(player.Xp);
        player.AddXp(xp);

        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private async Task AddKillAsync(string killerId, double victimWeightKg)
    {
        var now = DateTimeOffset.UtcNow;
        _context.KillLogs.Add(new KillLog
        {
            Id = KillLog.GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            KillerId = killerId,
            VictimWeightKg = victimWeightKg,
        });
        await _context.SaveChangesAsync();
    }

    private async Task HideFromLeaderboardAsync(string playerId) =>
        await _preferences.SaveAsync(playerId, new PlayerPreferencesUpdate(
            NotifyServerStatus: true,
            NotifyQuestComplete: true,
            NotifyDinoDeath: false,
            ShowOnLeaderboard: false,
            PublicProfile: false));

    private async Task RecordPlaytimeAsync(string playerId, string species, TimeSpan length)
    {
        var startedAt = DateTimeOffset.UtcNow - length;
        var session = PlaySession.Open(playerId, species, startedAt);
        session.LastSeenAt = DateTimeOffset.UtcNow;
        session.Close(PlaySessionEndReason.Left, DateTimeOffset.UtcNow);
        _context.PlaySessions.Add(session);
        await _context.SaveChangesAsync();
    }

    // ── Normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task Ranks_ByScoreDescending()
    {
        await AddPlayerAsync("s1", "Vex", 100);
        await AddPlayerAsync("s2", "Kestrel", 300);
        await AddPlayerAsync("s3", "Nyx", 200);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Select(entry => entry.PlayerName), Is.EqualTo(new[] { "Kestrel", "Nyx", "Vex" }));
        Assert.That(board.Entries.Select(entry => entry.Rank), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task Score_IsXpPlusTheWeightOfEverythingKilled()
    {
        var hunter = await AddPlayerAsync("s1", "Vex", 1_000);
        await AddKillAsync(hunter.Id, 4_500.4);
        await AddKillAsync(hunter.Id, 500.6);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Single().Score, Is.EqualTo(6_001));
    }

    [Test]
    public async Task Species_IsTheMostPlayedOne()
    {
        var player = await AddPlayerAsync("s1", "Vex", 100);
        await RecordPlaytimeAsync(player.Id, "Dryosaurus", TimeSpan.FromHours(1));
        await RecordPlaytimeAsync(player.Id, "Ceratosaurus", TimeSpan.FromHours(3));

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Single().Species, Is.EqualTo("Ceratosaurus"));
    }

    // ── Ties ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Ties_ShareARankAndTheNextScoreSkipsThePlacesTheyUsedUp()
    {
        await AddPlayerAsync("s1", "Vex", 500);
        await AddPlayerAsync("s2", "Kestrel", 500);
        await AddPlayerAsync("s3", "Nyx", 500);
        await AddPlayerAsync("s4", "Sable", 100);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Select(entry => entry.Rank), Is.EqualTo(new[] { 1, 1, 1, 4 }));
    }

    [Test]
    public async Task Ties_OrderTheSameWayEveryTime()
    {
        // A board that reshuffles equal scores between two reads of identical data is how a leaderboard
        // gets a reputation for lying.
        await AddPlayerAsync("s1", "Vex", 500);
        await AddPlayerAsync("s2", "Kestrel", 500);
        await AddPlayerAsync("s3", "Nyx", 500);

        var first = await _service.BuildAsync(callerPlayerId: null, take: 10);
        var second = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(second.Entries.Select(entry => entry.PlayerName),
            Is.EqualTo(first.Entries.Select(entry => entry.PlayerName)));
    }

    // ── The caller ────────────────────────────────────────────────────────

    [Test]
    public async Task Self_IsReturnedEvenWhenTheCallerIsWellOutsideTheListedRange()
    {
        for (var i = 0; i < 20; i++)
            await AddPlayerAsync($"top-{i}", $"Top {i}", 10_000 - i);

        var caller = await AddPlayerAsync("s-caller", "Straggler", 1);

        var board = await _service.BuildAsync(caller.Id, take: 3);

        Assert.Multiple(() =>
        {
            Assert.That(board.Entries, Has.Count.EqualTo(3));
            Assert.That(board.Entries.Any(entry => entry.PlayerId == caller.Id), Is.False);
            Assert.That(board.Self, Is.Not.Null);
            Assert.That(board.Self!.Rank, Is.EqualTo(21));
            Assert.That(board.RankedPlayers, Is.EqualTo(21));
        });
    }

    [Test]
    public async Task Self_IsNullForAnAnonymousCaller()
    {
        await AddPlayerAsync("s1", "Vex", 100);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Self, Is.Null);
    }

    // ── ShowOnLeaderboard ─────────────────────────────────────────────────

    [Test]
    public async Task OptedOut_IsGoneFromTheListingButKeepsTheirOwnTrueRank()
    {
        var hidden = await AddPlayerAsync("s1", "Vex", 10_000);
        await AddPlayerAsync("s2", "Kestrel", 5_000);
        await AddPlayerAsync("s3", "Nyx", 1_000);
        await HideFromLeaderboardAsync(hidden.Id);

        var board = await _service.BuildAsync(hidden.Id, take: 10);

        Assert.Multiple(() =>
        {
            Assert.That(board.Entries.Any(entry => entry.PlayerId == hidden.Id), Is.False);
            Assert.That(board.Entries.Select(entry => entry.PlayerName), Is.EqualTo(new[] { "Kestrel", "Nyx" }));

            // Rank 1, not 3. Opting out of being displayed is not opting out of knowing where you stand.
            Assert.That(board.Self!.Rank, Is.EqualTo(1));
            Assert.That(board.Self.PubliclyListed, Is.False);
        });
    }

    [Test]
    public async Task OptedOut_LeavesTheRanksOfEveryoneElseAlone()
    {
        // Ranking over every player, hidden ones included, is what stops a stranger's setting change
        // from moving somebody else's rank.
        var hidden = await AddPlayerAsync("s1", "Vex", 10_000);
        await AddPlayerAsync("s2", "Kestrel", 5_000);
        await AddPlayerAsync("s3", "Nyx", 1_000);
        await HideFromLeaderboardAsync(hidden.Id);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Select(entry => entry.Rank), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public async Task OptedOut_IsHiddenFromEveryoneElseToo()
    {
        var hidden = await AddPlayerAsync("s1", "Vex", 10_000);
        var other = await AddPlayerAsync("s2", "Kestrel", 5_000);
        await HideFromLeaderboardAsync(hidden.Id);

        var board = await _service.BuildAsync(other.Id, take: 10);

        Assert.That(board.Entries.Any(entry => entry.PlayerId == hidden.Id), Is.False);
    }

    [Test]
    public async Task APlayerWithNoPreferencesRowIsListed()
    {
        // The default, declared once in PlayerPreferences.For and read the same way here.
        await AddPlayerAsync("s1", "Vex", 100);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Single().PubliclyListed, Is.True);
    }

    // ── The endpoint ──────────────────────────────────────────────────────

    [Test]
    public async Task Endpoint_MarksTheCallersOwnRowWhenTheyAreInTheListing()
    {
        var caller = await AddPlayerAsync("s1", "Vex", 10_000, userId: "user-1");
        await AddPlayerAsync("s2", "Kestrel", 5_000);

        var dto = await LeaderboardEndpoints.Get(
            TestPrincipal.Create("user-1"), _context, _service, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(dto.Entries.Single(entry => entry.IsCurrentUser).Player, Is.EqualTo("Vex"));
            Assert.That(dto.Self!.Rank, Is.EqualTo(1));
            Assert.That(dto.SelfIsListed, Is.True);
            Assert.That(caller.UserId, Is.EqualTo("user-1"));
        });
    }

    [Test]
    public async Task Endpoint_ASignedInAccountWithNoPlayerRowStillGetsTheBoard()
    {
        // The normal state for someone who has signed in but never linked Steam. Not an error.
        await AddPlayerAsync("s1", "Vex", 100);

        var dto = await LeaderboardEndpoints.Get(
            TestPrincipal.Create("user-with-no-player"), _context, _service, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(dto.Entries, Has.Count.EqualTo(1));
            Assert.That(dto.Self, Is.Null);
            Assert.That(dto.SelfIsListed, Is.False);
        });
    }

    [Test]
    public async Task Endpoint_AnAnonymousVisitorGetsTheBoard()
    {
        await AddPlayerAsync("s1", "Vex", 100);

        var dto = await LeaderboardEndpoints.Get(
            TestPrincipal.CreateAnonymous(), _context, _service, CancellationToken.None);

        Assert.That(dto.Entries, Has.Count.EqualTo(1));
        Assert.That(dto.Self, Is.Null);
    }

    // ── Negative ──────────────────────────────────────────────────────────

    [Test]
    public async Task AnEmptyPlayerTableIsAnEmptyBoard()
    {
        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.Multiple(() =>
        {
            Assert.That(board.Entries, Is.Empty);
            Assert.That(board.Self, Is.Null);
            Assert.That(board.RankedPlayers, Is.Zero);
        });
    }

    [Test]
    public async Task AnAbsurdPageSizeIsClamped()
    {
        for (var i = 0; i < 5; i++)
            await AddPlayerAsync($"s{i}", $"P{i}", 100 - i);

        var negative = await _service.BuildAsync(null, take: -10);
        var huge = await _service.BuildAsync(null, take: int.MaxValue);

        Assert.That(negative.Entries, Has.Count.EqualTo(1));
        Assert.That(huge.Entries, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task APlayerWithNoInGameNameGetsAFriendlyIdRatherThanASteamId()
    {
        // The KingOfTheHillEndpoints rule: a Steam id on an anonymous surface resolves to a public
        // Steam profile, which turns the board into a directory.
        var player = await AddPlayerAsync("76561198000000000", name: null!, xp: 100);

        var board = await _service.BuildAsync(callerPlayerId: null, take: 10);

        Assert.That(board.Entries.Single().PlayerName, Does.Not.Contain("76561198000000000"));
        Assert.That(board.Entries.Single().PlayerName, Does.Contain(Player.EncodeFriendlyId(player.FriendlyIdSeq)));
    }
}
