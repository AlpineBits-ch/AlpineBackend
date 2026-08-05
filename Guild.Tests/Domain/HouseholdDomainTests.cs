using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>Pure domain behaviour for the household modules: consent resolution, chore cadence,
/// quiet-hour deferral and the pantry restock guard.</summary>
[TestFixture]
public class HouseholdDomainTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // Decisions - consent, not majority
    // ══════════════════════════════════════════════════════════════════════════

    private static Decision MakeDecision(int? quorum = null, params string[] optionTitles)
    {
        var decision = Decision.Create(new CreateDecisionParams
        {
            ChannelId = "chan-1", GuildId = "guild-1", Title = "Cat?",
            CreatedByUserId = "anna", Quorum = quorum,
        });

        for (var i = 0; i < optionTitles.Length; i++)
        {
            decision.Options.Add(new DecisionOption
            {
                Id = $"opt-{i}", DecisionId = decision.Id, Title = optionTitles[i], Position = i,
            });
        }

        return decision;
    }

    private static void Vote(Decision decision, string userId, DecisionVoteKind kind, string? optionId, string? reason = null) =>
        decision.Votes.Add(new DecisionVote
        {
            DecisionId = decision.Id, UserId = userId, Kind = kind, OptionId = optionId, Reason = reason,
        });

    [Test]
    public void Resolve_UnanimousSupport_Decides()
    {
        var decision = MakeDecision(null, "Yes", "No");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "ben", DecisionVoteKind.Support, "opt-0");

        var (status, winner) = decision.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(DecisionStatus.Decided));
            Assert.That(winner, Is.EqualTo("opt-0"));
        });
    }

    /// <summary>The defining behaviour of the module: a single block beats any amount of support.
    /// If this ever starts passing as Decided, decisions have silently become a poll.</summary>
    [Test]
    public void Resolve_OneBlock_BeatsMajoritySupport()
    {
        var decision = MakeDecision(null, "Yes", "No");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "ben", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "cara", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "dan", DecisionVoteKind.Block, "opt-0", "I'm allergic");

        var (status, winner) = decision.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.Not.EqualTo(DecisionStatus.Decided),
                "three-to-one support must not carry an option somebody blocked");
            Assert.That(winner, Is.Null);
        });
    }

    [Test]
    public void Resolve_BlockOnOneOption_LetsAnotherWin()
    {
        var decision = MakeDecision(null, "Cat", "Dog");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-1");
        Vote(decision, "ben", DecisionVoteKind.Block, "opt-0", "allergic to cats");

        var (status, winner) = decision.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(DecisionStatus.Decided));
            Assert.That(winner, Is.EqualTo("opt-1"), "blocking the cat shouldn't block the dog");
        });
    }

    [Test]
    public void Resolve_EveryOptionBlocked_IsBlockedNotLeastHated()
    {
        var decision = MakeDecision(null, "Cat", "Dog");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "ben", DecisionVoteKind.Block, "opt-0", "allergic");
        Vote(decision, "cara", DecisionVoteKind.Block, "opt-1", "no space");

        var (status, winner) = decision.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(DecisionStatus.Blocked));
            Assert.That(winner, Is.Null, "'we couldn't agree' is a result, not a fallback winner");
        });
    }

    [Test]
    public void Resolve_WholeDecisionBlock_BlocksEverything()
    {
        var decision = MakeDecision(null, "Cat", "Dog");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "ben", DecisionVoteKind.Block, null, "no pets at all");

        var (status, _) = decision.Resolve();

        Assert.That(status, Is.EqualTo(DecisionStatus.Blocked));
    }

    [Test]
    public void Resolve_BelowQuorum_Expires()
    {
        var decision = MakeDecision(3, "Yes", "No");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-0");

        var (status, _) = decision.Resolve();

        Assert.That(status, Is.EqualTo(DecisionStatus.Expired));
    }

    [Test]
    public void Resolve_AbstentionsDoNotCountTowardQuorum()
    {
        var decision = MakeDecision(2, "Yes", "No");
        Vote(decision, "anna", DecisionVoteKind.Support, "opt-0");
        Vote(decision, "ben", DecisionVoteKind.Abstain, null);

        var (status, _) = decision.Resolve();

        Assert.That(status, Is.EqualTo(DecisionStatus.Expired),
            "an abstention is a non-answer, so it can't be what unlocks a decision");
    }

    [Test]
    public void Resolve_NoVotesAtAll_Expires()
    {
        var (status, _) = MakeDecision(null, "Yes", "No").Resolve();

        Assert.That(status, Is.EqualTo(DecisionStatus.Expired));
    }

    // ══════════════════════════════════════════════════════════════════════════ Chore cadence
    // ══════════════════════════════════════════════════════════════════════════

    private static Chore MakeChore(DateTimeOffset anchor, int intervalDays = 7) =>
        Chore.Create(new CreateChoreParams
        {
            ChannelId = "chan-1", GuildId = "guild-1", Title = "Bins",
            IntervalDays = intervalDays, AnchorAt = anchor, FixedAssigneeUserId = "anna",
        });

    [Test]
    public void AdvanceFrom_StepsOnTheAnchorCadence()
    {
        var anchor = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var chore = MakeChore(anchor);

        Assert.That(chore.AdvanceFrom(anchor), Is.EqualTo(anchor.AddDays(7)));
    }

    /// <summary>Completing three days late must not shift bin day forever - the schedule steps
    /// from the anchor, not from when the work actually happened.</summary>
    [Test]
    public void AdvanceFrom_LateCompletion_DoesNotDriftTheSchedule()
    {
        var anchor = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var chore = MakeChore(anchor);

        var next = chore.AdvanceFrom(anchor.AddDays(3));   // done three days late

        Assert.That(next, Is.EqualTo(anchor.AddDays(7)), "still the original weekly slot");
    }

    [Test]
    public void AdvanceFrom_LongGap_SkipsToTheNextFutureSlot()
    {
        var anchor = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var chore = MakeChore(anchor);

        var next = chore.AdvanceFrom(anchor.AddDays(30));

        Assert.Multiple(() =>
        {
            Assert.That(next, Is.GreaterThan(anchor.AddDays(30)));
            Assert.That((next - anchor).TotalDays % 7, Is.EqualTo(0).Within(0.0001),
                "must stay on the original cadence rather than resetting to 'now'");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Quiet hours
    // ══════════════════════════════════════════════════════════════════════════

    private static GuildQuietHoursConfig QuietHours(bool enabled = true, int start = 22 * 60, int end = 7 * 60) =>
        new() { GuildId = "guild-1", Enabled = enabled, StartMinuteLocal = start, EndMinuteLocal = end, TimeZoneId = "UTC" };

    [Test]
    public void DeferPast_OutsideWindow_LeavesInstantAlone()
    {
        var midday = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.That(QuietHours().DeferPast(midday), Is.EqualTo(midday));
    }

    [Test]
    public void DeferPast_InsideWrappingWindow_MovesToWindowEnd()
    {
        // 23:50 is inside a 22:00-07:00 window that wraps midnight.
        var lateNight = new DateTimeOffset(2026, 3, 1, 23, 50, 0, TimeSpan.Zero);

        var deferred = QuietHours().DeferPast(lateNight);

        Assert.That(deferred, Is.EqualTo(new DateTimeOffset(2026, 3, 2, 7, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void DeferPast_EarlyMorningSideOfWindow_MovesToSameDayEnd()
    {
        var earlyMorning = new DateTimeOffset(2026, 3, 1, 2, 30, 0, TimeSpan.Zero);

        var deferred = QuietHours().DeferPast(earlyMorning);

        Assert.That(deferred, Is.EqualTo(new DateTimeOffset(2026, 3, 1, 7, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void DeferPast_Disabled_IsAlwaysIdentity()
    {
        var lateNight = new DateTimeOffset(2026, 3, 1, 23, 50, 0, TimeSpan.Zero);

        Assert.That(QuietHours(enabled: false).DeferPast(lateNight), Is.EqualTo(lateNight));
    }

    [Test]
    public void DeferPast_UnknownTimeZone_DegradesToNoDeferral()
    {
        // A bad tz id must never swallow a reminder entirely.
        var config = QuietHours();
        config.TimeZoneId = "Mars/Olympus_Mons";
        var lateNight = new DateTimeOffset(2026, 3, 1, 23, 50, 0, TimeSpan.Zero);

        Assert.That(config.DeferPast(lateNight), Is.EqualTo(lateNight));
    }

    // ══════════════════════════════════════════════════════════════════════════ Pantry restock
    // guard ══════════════════════════════════════════════════════════════════════════

    private static PantryItem MakePantryItem(decimal quantity, decimal? threshold) =>
        PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = "chan-1", GuildId = "guild-1", Name = "Milk",
            Quantity = quantity, LowThreshold = threshold, AddedByUserId = "anna",
        });

    [Test]
    public void NeedsRestock_AtOrBelowThreshold_IsTrue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MakePantryItem(1, 1).NeedsRestock(), Is.True, "at the threshold counts as low");
            Assert.That(MakePantryItem(0, 1).NeedsRestock(), Is.True);
            Assert.That(MakePantryItem(2, 1).NeedsRestock(), Is.False);
        });
    }

    [Test]
    public void NeedsRestock_WithoutThreshold_IsNeverTrue()
    {
        Assert.That(MakePantryItem(0, null).NeedsRestock(), Is.False,
            "no threshold means the member opted out of restock tracking for this item");
    }

    /// <summary>The idempotency guard.</summary>
    [Test]
    public void NeedsRestock_AlreadyRestocked_IsFalseUntilReleased()
    {
        var item = MakePantryItem(1, 2);
        Assert.That(item.NeedsRestock(), Is.True);

        item.RestockedAt = DateTimeOffset.UtcNow;
        Assert.That(item.NeedsRestock(), Is.False, "already on the list - must not be added twice");

        item.RestockedAt = null;
        Assert.That(item.NeedsRestock(), Is.True, "released once bought or removed from the list");
    }
}
