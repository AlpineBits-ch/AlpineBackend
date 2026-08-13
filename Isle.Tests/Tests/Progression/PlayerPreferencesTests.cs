using Isle.Api.Endpoints;
using Isle.Api.Services.Progression;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Progression;

/// <summary>The isle-scoped settings, and the surfaces that have to honour them.</summary>
[TestFixture]
public class PlayerPreferencesTests
{
    private TestIsleContext _context = null!;
    private PlayerPreferencesService _service = null!;
    private PlaySessionTracker _sessions = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _service = new PlayerPreferencesService(_context);
        _sessions = new PlaySessionTracker(_context, NullLogger<PlaySessionTracker>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Player> AddPlayerAsync(string steamId = "steam-1", string? userId = null, string? name = "Vex")
    {
        var player = TestData.Player(steamId, name);
        player.UserId = userId;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private static PlayerPreferencesDto Body(bool showOnLeaderboard = true, bool publicProfile = false) => new()
    {
        NotifyServerStatus = true,
        NotifyQuestComplete = true,
        NotifyDinoDeath = true,
        ShowOnLeaderboard = showOnLeaderboard,
        PublicProfile = publicProfile,
    };

    // ── Defaults ──────────────────────────────────────────────────────────

    [Test]
    public async Task APlayerWithNoRowGetsTheDeclaredDefaults()
    {
        var player = await AddPlayerAsync();

        var preferences = await _service.GetAsync(player.Id);
        var rows = await _context.PlayerPreferences.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(preferences.ShowOnLeaderboard, Is.True);
            Assert.That(preferences.PublicProfile, Is.False);
            Assert.That(preferences.NotifyDinoDeath, Is.False);
            Assert.That(rows, Is.Zero,
                "reading must not create a row - the default is the answer, not a side effect");
        });
    }

    [Test]
    public async Task ABatchReadAnswersForEveryRequestedPlayer()
    {
        // A caller that has to tell "absent" from "unrestricted" will get it wrong eventually, and
        // getting it wrong on a privacy flag fails open.
        var stored = await AddPlayerAsync("steam-1");
        var missing = await AddPlayerAsync("steam-2");
        await _service.SaveAsync(stored.Id, new PlayerPreferencesUpdate(true, true, true, false, true));

        var all = await _service.GetAsync([stored.Id, missing.Id]);

        Assert.Multiple(() =>
        {
            Assert.That(all.Keys, Is.EquivalentTo(new[] { stored.Id, missing.Id }));
            Assert.That(all[stored.Id].ShowOnLeaderboard, Is.False);
            Assert.That(all[missing.Id].ShowOnLeaderboard, Is.True);
        });
    }

    // ── Round trip ────────────────────────────────────────────────────────

    [Test]
    public async Task SavingCreatesTheRowAndReadsBack()
    {
        var player = await AddPlayerAsync(userId: "user-1");

        var saved = await PlayerPreferencesEndpoints.Save(
            Body(showOnLeaderboard: false, publicProfile: true),
            TestPrincipal.Create("user-1"), _context, _service, CancellationToken.None);

        var read = await PlayerPreferencesEndpoints.Get(
            TestPrincipal.Create("user-1"), _context, _service, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(saved, Is.InstanceOf<Ok<PlayerPreferencesDto>>());
            Assert.That(read.ShowOnLeaderboard, Is.False);
            Assert.That(read.PublicProfile, Is.True);
            Assert.That(read.NotifyDinoDeath, Is.True);
            Assert.That(player.Id, Is.Not.Empty);
        });
    }

    [Test]
    public async Task SavingTwiceUpdatesTheSameRow()
    {
        var player = await AddPlayerAsync(userId: "user-1");

        await _service.SaveAsync(player.Id, new PlayerPreferencesUpdate(true, true, true, false, true));
        await _service.SaveAsync(player.Id, new PlayerPreferencesUpdate(false, false, false, true, false));

        Assert.That(await _context.PlayerPreferences.CountAsync(), Is.EqualTo(1));
        Assert.That((await _service.GetAsync(player.Id)).ShowOnLeaderboard, Is.True);
    }

    // ── PublicProfile enforcement ─────────────────────────────────────────

    [Test]
    public async Task APublicProfileIsReadableByAnyone()
    {
        var player = await AddPlayerAsync();
        await _service.SaveAsync(player.Id, new PlayerPreferencesUpdate(true, true, true, true, PublicProfile: true));

        var result = await PublicProfileEndpoints.Get(
            player.FriendlyId, _context, _service, _sessions, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Ok<PublicProfileDto>>());
        Assert.That(((Ok<PublicProfileDto>)result).Value!.Player, Is.EqualTo("Vex"));
    }

    [Test]
    public async Task ProfilesAreOffByDefault()
    {
        // The only defensible default for a surface that needs no authentication to read.
        var player = await AddPlayerAsync();

        var result = await PublicProfileEndpoints.Get(
            player.FriendlyId, _context, _service, _sessions, CancellationToken.None);

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [Test]
    public async Task AProfileTurnedOffAnswersExactlyAsOneThatDoesNotExist()
    {
        // A distinct "this player has opted out" would confirm the account to anyone guessing ids.
        var player = await AddPlayerAsync();
        await _service.SaveAsync(player.Id, new PlayerPreferencesUpdate(true, true, true, true, PublicProfile: false));

        var optedOut = await PublicProfileEndpoints.Get(
            player.FriendlyId, _context, _service, _sessions, CancellationToken.None);
        var nonexistent = await PublicProfileEndpoints.Get(
            Player.EncodeFriendlyId(999999), _context, _service, _sessions, CancellationToken.None);

        Assert.That(StatusOf(optedOut), Is.EqualTo(StatusOf(nonexistent)));
    }

    [Test]
    public async Task APublicProfileCarriesNothingLiveAndNoSteamId()
    {
        var player = await AddPlayerAsync("76561198000000000");
        await _service.SaveAsync(player.Id, new PlayerPreferencesUpdate(true, true, true, true, PublicProfile: true));

        var result = (Ok<PublicProfileDto>)await PublicProfileEndpoints.Get(
            player.FriendlyId, _context, _service, _sessions, CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.That(json, Does.Not.Contain("76561198000000000"));
        Assert.That(json, Does.Not.Contain(player.Id));
    }

    [Test]
    public async Task AMalformedFriendlyIdIsNotFoundRatherThanAnError()
    {
        Assert.That(StatusOf(await PublicProfileEndpoints.Get(
            "not-an-id", _context, _service, _sessions, CancellationToken.None)),
            Is.EqualTo(StatusCodes.Status404NotFound));
    }

    // ── The unlinked account ──────────────────────────────────────────────

    [Test]
    public async Task ReadingSettingsWithNoLinkedPlayerAnswersWithTheDefaults()
    {
        // The settings page is one of the first things a new user opens, before they have ever joined.
        var read = await PlayerPreferencesEndpoints.Get(
            TestPrincipal.Create("user-with-no-player"), _context, _service, CancellationToken.None);

        Assert.That(read.ShowOnLeaderboard, Is.True);
        Assert.That(read.PublicProfile, Is.False);
    }

    [Test]
    public async Task SavingWithNoLinkedPlayerIsRefusedRatherThanSilentlyDropped()
    {
        var result = await PlayerPreferencesEndpoints.Save(
            Body(), TestPrincipal.Create("user-with-no-player"), _context, _service, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(await _context.PlayerPreferences.CountAsync(), Is.Zero);
    }

    private static int? StatusOf(IResult result) => result as IStatusCodeHttpResult is { } coded
        ? coded.StatusCode
        : null;
}
