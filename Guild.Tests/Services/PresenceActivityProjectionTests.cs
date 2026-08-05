using Guild.Application.Bus.Events.Realtime;
using Guild.Application.Services;
using Identity.Contracts.Bus.Response;
using Social.Contracts.Dtos;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Services;

/// <summary>
/// The two rules that decide whether an activity reaches another user, and the one that decides
/// whether "for 23 minutes" is true.
/// </summary>
[TestFixture]
public class PresenceActivityProjectionTests
{
    private static ActivityDto Playing(string name = "Overwatch", string? appId = "1", long? startedAt = null) => new()
    {
        Type = "Playing",
        Source = "Rpc",
        Name = name,
        ApplicationId = appId,
        StartedAt = startedAt,
    };

    // ── Projection: who may see an activity ─────────────────────────────────────────────────

    [Test]
    public void ProjectActivitiesFor_OrdinaryViewer_SeesTheActivity()
    {
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], OnlineStatus.Online, viewerIsSubject: false, shareActivity: true);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Overwatch"));
    }

    [Test]
    public void ProjectActivitiesFor_ShareActivityOff_HidesFromOthers()
    {
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], OnlineStatus.Online, viewerIsSubject: false, shareActivity: false);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ProjectActivitiesFor_HiddenStatus_HidesFromOthers()
    {
        // Broadcasting "Playing Overwatch" while appearing offline tells a viewer both that the
        // user is online and what they are doing - strictly more than the status leak this class
        // was written to close.
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], OnlineStatus.Hidden, viewerIsSubject: false, shareActivity: true);

        Assert.That(result, Is.Empty);
    }

    [TestCase(true, OnlineStatus.Hidden)]
    [TestCase(false, OnlineStatus.Online)]
    [TestCase(false, OnlineStatus.Hidden)]
    public void ProjectActivitiesFor_Subject_AlwaysSeesTheirOwn(bool shareActivity, OnlineStatus status)
    {
        // A client that could not read its own activity back could not render the settings that
        // control it.
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], status, viewerIsSubject: true, shareActivity);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void ProjectActivitiesFor_NullOrEmpty_ReturnsEmptyNotNull()
    {
        Assert.That(PresenceProjection.ProjectActivitiesFor(null, OnlineStatus.Online, false, true), Is.Empty);
        Assert.That(PresenceProjection.ProjectActivitiesFor([], OnlineStatus.Online, false, true), Is.Empty);
    }

    [Test]
    public void ProjectActivitiesFor_UnparseableStatusName_DoesNotThrowAndStillHonoursShareActivity()
    {
        var visible = PresenceProjection.ProjectActivitiesFor([Playing()], "NotAStatus", false, shareActivity: true);
        var hidden = PresenceProjection.ProjectActivitiesFor([Playing()], "NotAStatus", false, shareActivity: false);

        Assert.That(visible, Has.Count.EqualTo(1));
        Assert.That(hidden, Is.Empty);
    }

    [Test]
    public void ProjectActivitiesFor_HiddenByName_HidesFromOthers()
    {
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], nameof(OnlineStatus.Hidden), viewerIsSubject: false, shareActivity: true);

        Assert.That(result, Is.Empty);
    }

    // ── Per-application opt-out ─────────────────────────────────────────────────────────────

    private static HiddenActivitySummary Hiding(string[]? applicationIds = null, string[]? names = null) => new()
    {
        ApplicationIds = applicationIds ?? [],
        Names = names ?? [],
    };

    [Test]
    public void ProjectActivitiesFor_SuppressedApplicationId_IsDroppedForOthers()
    {
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing("Overwatch", appId: "1"), Playing("Dota 2", appId: "2")],
            OnlineStatus.Online, viewerIsSubject: false, shareActivity: true,
            Hiding(applicationIds: ["1"]));

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Dota 2"));
    }

    [Test]
    public void ProjectActivitiesFor_SuppressedName_IsDroppedCaseInsensitively()
    {
        // The name key exists for sources that produce no application id, and it is a display
        // string the user never typed - whichever source emitted it chose the casing.
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing("Some Indie Game", appId: null)],
            OnlineStatus.Online, viewerIsSubject: false, shareActivity: true,
            Hiding(names: ["sOmE iNdIe GaMe"]));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ProjectActivitiesFor_SuppressionList_DoesNotHideTheSubjectsOwn()
    {
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing("Overwatch", appId: "1")],
            OnlineStatus.Online, viewerIsSubject: true, shareActivity: true,
            Hiding(applicationIds: ["1"]));

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void ProjectActivitiesFor_NonMatchingSuppression_ChangesNothing()
    {
        var activities = new[] { Playing("Overwatch", appId: "1") };

        var result = PresenceProjection.ProjectActivitiesFor(
            activities, OnlineStatus.Online, viewerIsSubject: false, shareActivity: true,
            Hiding(applicationIds: ["999"], names: ["Something Else"]));

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Is.SameAs(activities), "an untouched list should not be rebuilt");
    }

    [Test]
    public void ProjectActivitiesFor_UnresolvablePrivacyRecord_WithholdsRatherThanPublishes()
    {
        // What PrivacySettingsCache.Restrictive hands back when Identity cannot be reached:
        // ShareActivity off, and an empty suppression set because "which games did they hide" has
        // no honest answer.
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], OnlineStatus.Online, viewerIsSubject: false, shareActivity: false, Hiding());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ProjectActivitiesFor_EmptySuppressionSet_ChangesNothing()
    {
        var result = PresenceProjection.ProjectActivitiesFor(
            [Playing()], OnlineStatus.Online, viewerIsSubject: false, shareActivity: true, Hiding());

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void Suppresses_MatchesApplicationIdOrdinallyAndNameCaseInsensitively()
    {
        var hidden = Hiding(applicationIds: ["730"], names: ["Spotify"]);

        Assert.Multiple(() =>
        {
            Assert.That(hidden.Suppresses("730", "anything"), Is.True);
            Assert.That(hidden.Suppresses(null, "spotify"), Is.True);
            Assert.That(hidden.Suppresses("731", "Overwatch"), Is.False);
            Assert.That(hidden.Suppresses(null, null), Is.False);
            Assert.That(hidden.Suppresses("", ""), Is.False, "empty is not a key and must not match");
        });
    }

    // ── Sticky start time: what makes the elapsed timer true ────────────────────────────────

    [Test]
    public void MergeStartTimes_SameActivity_KeepsTheOriginalStart()
    {
        // This is the whole feature.
        var originalStart = 1_000L;
        var previous = new[] { Playing(startedAt: originalStart) };
        var incoming = new[] { Playing(startedAt: 999_999L) };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, incoming, nowMs: 500_000L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(originalStart),
            "a client re-sending its own start must not be able to restart the clock");
    }

    [Test]
    public void MergeStartTimes_SameGameWithChangedDetailsAndState_KeepsTheOriginalStart()
    {
        // The in-game update case: "In Queue" becomes "Competitive - Mirage" while the same session
        // continues.
        var previous = new[]
        {
            new ActivityDto
            {
                Type = "Playing", Source = "Rpc", Name = "Counter-Strike 2", ApplicationId = "730",
                Details = "In Queue", State = "Searching", StartedAt = 1_000L,
                Party = new ActivityPartyDto { Size = 3, Max = 5 },
            },
        };

        var incoming = new[]
        {
            new ActivityDto
            {
                Type = "Playing", Source = "Rpc", Name = "Counter-Strike 2", ApplicationId = "730",
                Details = "Competitive - Mirage", State = "Round 4 of 24", StartedAt = null,
                Party = new ActivityPartyDto { Size = 5, Max = 5 },
            },
        };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, incoming, nowMs: 900_000L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(1_000L), "an in-game state change is not a new session");
        Assert.That(merged[0].Details, Is.EqualTo("Competitive - Mirage"), "but the new detail must be what ships");
        Assert.That(merged[0].State, Is.EqualTo("Round 4 of 24"));
        Assert.That(merged[0].Party!.Size, Is.EqualTo(5));
    }

    [Test]
    public void MergeStartTimes_DifferentGame_TakesANewStart()
    {
        var previous = new[] { Playing("Overwatch", appId: "1", startedAt: 1_000L) };
        var incoming = new[] { Playing("Dota 2", appId: "2", startedAt: null) };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, incoming, nowMs: 500_000L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(500_000L));
    }

    [Test]
    public void MergeStartTimes_NoPriorAndNoClientValue_FallsBackToServerTime()
    {
        var merged = GuildLifecycleHandler.MergeStartTimes(null, [Playing(startedAt: null)], nowMs: 42L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(42L),
            "server receive time is the one value no client can lie about");
    }

    [Test]
    public void MergeStartTimes_NewActivityWithClientValue_KeepsIt()
    {
        // Already range-checked by ActivityWriteGuard, so it is believable here.
        var merged = GuildLifecycleHandler.MergeStartTimes(null, [Playing(startedAt: 123L)], nowMs: 999L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(123L));
    }

    [Test]
    public void MergeStartTimes_SameNameDifferentApplication_DoesNotInheritTheStart()
    {
        var previous = new[] { Playing("Overwatch", appId: "1", startedAt: 1_000L) };
        var incoming = new[] { Playing("Overwatch", appId: "2", startedAt: null) };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, incoming, nowMs: 500_000L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(500_000L));
    }

    [Test]
    public void MergeStartTimes_AdjacentFieldsCannotCollideIntoOneKey()
    {
        // ("Playing", "AB", null) and ("PlayingA", "B", null) concatenate identically.
        var previous = new[] { new ActivityDto { Type = "Playing", Source = "Rpc", Name = "AB", ApplicationId = null, StartedAt = 1_000L } };
        var incoming = new[] { new ActivityDto { Type = "PlayingA", Source = "Rpc", Name = "B", ApplicationId = null, StartedAt = null } };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, incoming, nowMs: 777L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(777L));
    }

    [Test]
    public void MergeStartTimes_DoesNotMutateTheIncomingList()
    {
        // The incoming list belongs to the bus message and the caller writes the result once per
        // guild.
        var incoming = new[] { Playing(startedAt: null) };
        var previous = new[] { Playing(startedAt: 1_000L) };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, incoming, nowMs: 500_000L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(1_000L));
        Assert.That(incoming[0].StartedAt, Is.Null, "the source list must be left exactly as it was");
        Assert.That(merged[0], Is.Not.SameAs(incoming[0]));
    }

    [Test]
    public void MergeStartTimes_ConflictingPriorStarts_TakesTheFirstDeterministically()
    {
        // Entries for the same user across guilds are written together and so agree; if they ever
        // diverge the answer must not depend on enumeration order.
        var previous = new[] { Playing(startedAt: 1_000L), Playing(startedAt: 9_000L) };

        var merged = GuildLifecycleHandler.MergeStartTimes(previous, [Playing(startedAt: null)], nowMs: 500_000L);

        Assert.That(merged[0].StartedAt, Is.EqualTo(1_000L));
    }

    [Test]
    public void MergeStartTimes_EmptyIncoming_ClearsEverything()
    {
        var merged = GuildLifecycleHandler.MergeStartTimes([Playing(startedAt: 1_000L)], [], nowMs: 500L);

        Assert.That(merged, Is.Empty);
    }
}
