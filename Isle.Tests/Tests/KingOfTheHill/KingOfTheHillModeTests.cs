using Isle.Api.Games.KingOfTheHill;
using Isle.Api.Services.KingOfTheHill;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.Interfaces;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillModeTests
{
    private TestIsleContext _context = null!;
    private KingOfTheHillControlLedger _ledger = null!;
    private KingOfTheHillMode _mode = null!;
    private GameModeInstance _instance = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(), NullLogger<KingOfTheHillControlLedger>.Instance);
        _mode = new KingOfTheHillMode(_ledger, _context, NullLogger<KingOfTheHillMode>.Instance);

        var definition = TestData.KothDefinition();
        _instance = new GameModeInstance(definition, Substitute.For<IGameMode>());
        _instance.Start();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void GetStandings_NoLedgerEntries_ReturnsEmpty()
    {
        Assert.That(_mode.GetStandings(_instance), Is.Empty);
    }

    [Test]
    public async Task GetStandings_RanksCreditedPlayersByTicks_AndResolvesToPlayerIds()
    {
        var winner = TestData.Player("steam_winner");
        var runnerUp = TestData.Player("steam_runnerup");
        _context.Players.AddRange(winner, runnerUp);
        await _context.SaveChangesAsync();

        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_winner"]);
        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_winner"]);
        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_runnerup"]);

        var standings = _mode.GetStandings(_instance);

        Assert.That(standings, Has.Count.EqualTo(2));
        Assert.That(standings[0].PlayerId, Is.EqualTo(winner.Id));
        Assert.That(standings[0].Rank, Is.EqualTo(1));
        Assert.That(standings[0].Score, Is.EqualTo(2));
        Assert.That(standings[1].PlayerId, Is.EqualTo(runnerUp.Id));
        Assert.That(standings[1].Rank, Is.EqualTo(2));
    }

    [Test]
    public async Task GetStandings_SkipsSteamIdsWithNoRegisteredPlayer()
    {
        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_unregistered"]);

        Assert.That(_mode.GetStandings(_instance), Is.Empty);
    }

    [Test]
    public void GetRewards_NonWinner_ReturnsNoBonus()
    {
        var standing = new ParticipantStanding { PlayerId = "p1", Score = 5, Rank = 2, CustomMetrics = new() };

        Assert.That(_mode.GetRewards(_instance, standing), Is.Empty);
    }

    [Test]
    public async Task GetRewards_WinnerWithNoOtherContestants_ReturnsNoBonus()
    {
        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_winner"]);
        var standing = new ParticipantStanding { PlayerId = "p1", Score = 1, Rank = 1, CustomMetrics = new() };

        Assert.That(_mode.GetRewards(_instance, standing), Is.Empty);
    }

    [Test]
    public async Task GetRewards_WinnerWithOtherContestants_ScalesXpByContestantCount()
    {
        // Three distinct steam ids ever entered the zone (contested together), so the winner's bonus
        // should scale by (3 - 1) = 2 other contestants.
        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_winner", "steam_2", "steam_3"]);
        var standing = new ParticipantStanding { PlayerId = "p1", Score = 1, Rank = 1, CustomMetrics = new() };

        var rewards = _mode.GetRewards(_instance, standing);

        Assert.That(rewards, Has.Count.EqualTo(1));
        Assert.That(rewards[0].RewardType, Is.EqualTo(RewardType.Xp));
        Assert.That(rewards[0].AppliesTo, Is.EqualTo(RankRequirement.Winner));
        Assert.That(rewards[0].Amount, Is.EqualTo(300 * 2));
    }

    [Test]
    public async Task GetStandings_DuplicateSteamIdAcrossTwoPlayerRows_PicksOneRatherThanThrowing()
    {
        // Nothing constrains SteamId to be unique at the DB level; GetStandings groups rather than
        // dictionary-keys straight off it for exactly this reason.
        var first = TestData.Player("steam_shared");
        var second = TestData.Player("steam_shared");
        _context.Players.AddRange(first, second);
        await _context.SaveChangesAsync();

        await _ledger.ApplyPresenceAsync(_instance.InstanceId, ["steam_shared"]);

        var standings = _mode.GetStandings(_instance);

        Assert.That(standings, Has.Count.EqualTo(1));
        Assert.That(standings[0].PlayerId, Is.EqualTo(first.Id).Or.EqualTo(second.Id));
    }

    [Test]
    public void GetRewards_WinnerWithZeroContestantsAtAll_ReturnsNoBonusRatherThanANegativeAmount()
    {
        // Nobody was ever recorded in the contestants set (contestants == 0), as opposed to the
        // "winner with no other contestants" case where the winner themself makes it 1 — the
        // Math.Max(0, contestants - 1) floor must hold here too, or this would compute a negative XP amount.
        var standing = new ParticipantStanding { PlayerId = "p1", Score = 0, Rank = 1, CustomMetrics = new() };

        Assert.That(_mode.GetRewards(_instance, standing), Is.Empty);
    }
}
