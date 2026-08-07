using Guild.Domain.Entity;

namespace Guild.Tests.Domain;

/// <summary>
/// Pure interval arithmetic for absences: what an absence covers, when two of them collide, and how
/// much of a window a member was actually in the house for.
/// </summary>
[TestFixture]
public class AbsenceDomainTests
{
    private static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MemberAbsence Away(int startDay, int endDay, string userId = "anna") =>
        MemberAbsence.Create(new CreateMemberAbsenceParams
        {
            GuildId = "guild-1", UserId = userId, CreatedByUserId = userId,
            StartAt = Jan1.AddDays(startDay), EndAt = Jan1.AddDays(endDay),
        });

    // ══════════════════════════════════════════════════════════════════════════ Covers
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Covers_InstantInsideTheWindow_IsTrue() =>
        Assert.That(Away(5, 10).Covers(Jan1.AddDays(7)), Is.True);

    /// <summary>Start is inclusive and end is exclusive, which is what makes two back-to-back
    /// absences describe one continuous stretch rather than one with a duplicated instant in it.</summary>
    [Test]
    public void Covers_TreatsStartAsInsideAndEndAsOutside()
    {
        var absence = Away(5, 10);

        Assert.Multiple(() =>
        {
            Assert.That(absence.Covers(Jan1.AddDays(5)), Is.True, "the first moment is away");
            Assert.That(absence.Covers(Jan1.AddDays(10)), Is.False, "you are back by the end instant");
        });
    }

    [Test]
    public void Covers_InstantOutsideTheWindow_IsFalse()
    {
        var absence = Away(5, 10);

        Assert.Multiple(() =>
        {
            Assert.That(absence.Covers(Jan1.AddDays(4)), Is.False);
            Assert.That(absence.Covers(Jan1.AddDays(11)), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Overlaps
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Overlaps_PartiallyCoincidingWindows_IsTrue() =>
        Assert.That(Away(5, 10).Overlaps(Jan1.AddDays(8), Jan1.AddDays(12)), Is.True);

    [Test]
    public void Overlaps_ContainedWindow_IsTrue() =>
        Assert.That(Away(5, 20).Overlaps(Jan1.AddDays(8), Jan1.AddDays(9)), Is.True);

    /// <summary>Touching at an endpoint is not an overlap.</summary>
    [Test]
    public void Overlaps_AbuttingWindows_IsFalse()
    {
        var absence = Away(5, 10);

        Assert.Multiple(() =>
        {
            Assert.That(absence.Overlaps(Jan1.AddDays(10), Jan1.AddDays(14)), Is.False);
            Assert.That(absence.Overlaps(Jan1.AddDays(1), Jan1.AddDays(5)), Is.False);
        });
    }

    [Test]
    public void Overlaps_DisjointWindows_IsFalse() =>
        Assert.That(Away(5, 10).Overlaps(Jan1.AddDays(20), Jan1.AddDays(25)), Is.False);

    // ══════════════════════════════════════════════════════════════════════════ AbsentDaysWithin
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void AbsentDaysWithin_WholeAbsenceInsideTheWindow_CountsAllOfIt()
    {
        var days = MemberAbsence.AbsentDaysWithin([Away(5, 12)], Jan1, Jan1.AddDays(30));

        Assert.That(days, Is.EqualTo(7).Within(0.0001));
    }

    [Test]
    public void AbsentDaysWithin_NoAbsences_IsZero() =>
        Assert.That(MemberAbsence.AbsentDaysWithin([], Jan1, Jan1.AddDays(30)), Is.Zero);

    /// <summary>A holiday that started before the balance window opened only counts for the part
    /// inside it - otherwise a long absence would credit a member with more presence-free days than
    /// the window even has.</summary>
    [Test]
    public void AbsentDaysWithin_ClampsToTheWindow()
    {
        var days = MemberAbsence.AbsentDaysWithin(
            [Away(-20, 5)], Jan1, Jan1.AddDays(30));

        Assert.That(days, Is.EqualTo(5).Within(0.0001));
    }

    [Test]
    public void AbsentDaysWithin_AbsenceEntirelyOutsideTheWindow_IsZero()
    {
        var days = MemberAbsence.AbsentDaysWithin(
            [Away(40, 50)], Jan1, Jan1.AddDays(30));

        Assert.That(days, Is.Zero);
    }

    /// <summary>The guard that stops a balance from being movable by entering the same fortnight
    /// twice. Overlaps are rejected on write, but the arithmetic must not depend on that having
    /// worked.</summary>
    [Test]
    public void AbsentDaysWithin_OverlappingAbsences_AreMergedNotSummed()
    {
        var days = MemberAbsence.AbsentDaysWithin(
            [Away(5, 12), Away(8, 15)], Jan1, Jan1.AddDays(30));

        Assert.That(days, Is.EqualTo(10).Within(0.0001),
            "5 to 15 is ten days, not the seven plus seven of counting them separately");
    }

    [Test]
    public void AbsentDaysWithin_DisjointAbsences_AreAddedUp()
    {
        var days = MemberAbsence.AbsentDaysWithin(
            [Away(5, 8), Away(20, 24)], Jan1, Jan1.AddDays(30));

        Assert.That(days, Is.EqualTo(7).Within(0.0001));
    }

    [Test]
    public void AbsentDaysWithin_AbsencesGivenOutOfOrder_StillMerge()
    {
        var days = MemberAbsence.AbsentDaysWithin(
            [Away(20, 24), Away(5, 12), Away(8, 15)], Jan1, Jan1.AddDays(30));

        Assert.That(days, Is.EqualTo(14).Within(0.0001));
    }

    [Test]
    public void AbsentDaysWithin_InvertedWindow_IsZero() =>
        Assert.That(MemberAbsence.AbsentDaysWithin([Away(5, 12)], Jan1.AddDays(30), Jan1), Is.Zero,
            "an empty window contains no days, absent or otherwise");
}
