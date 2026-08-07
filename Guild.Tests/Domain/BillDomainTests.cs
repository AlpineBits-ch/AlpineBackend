using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// Pure domain behaviour for recurring bills: the calendar cadence, the backlog collapse and the
/// announcement stamp.
/// </summary>
[TestFixture]
public class BillDomainTests
{
    private static RecurringExpense MakeTemplate(
        DateTimeOffset anchor,
        RecurrenceUnit unit = RecurrenceUnit.Month,
        int interval = 1,
        long? amountMinor = 85000,
        int leadDays = RecurringExpense.DefaultLeadDays) =>
        RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = "chan-1", GuildId = "guild-1", Description = "Rent",
            AmountMinor = amountMinor, PayerUserId = "anna", Category = ExpenseCategory.Rent,
            RecurrenceUnit = unit, RecurrenceInterval = interval, AnchorAt = anchor,
            LeadDays = leadDays, CreatedByUserId = "anna",
        });

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 9) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    // ══════════════════════════════════════════════════════════════════════════ Calendar cadence
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void SlotAt_Monthly_StaysOnTheDayOfTheMonth()
    {
        var template = MakeTemplate(Utc(2026, 1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(template.SlotAt(1), Is.EqualTo(Utc(2026, 2, 1)));
            Assert.That(template.SlotAt(2), Is.EqualTo(Utc(2026, 3, 1)));
            Assert.That(template.SlotAt(12), Is.EqualTo(Utc(2027, 1, 1)));
        });
    }

    /// <summary>The reason RecurrenceUnit exists at all.</summary>
    [Test]
    public void SlotAt_MonthlyFromTheThirtyFirst_ClampsAndThenSnapsBack()
    {
        var template = MakeTemplate(Utc(2026, 1, 31));

        Assert.Multiple(() =>
        {
            Assert.That(template.SlotAt(1), Is.EqualTo(Utc(2026, 2, 28)), "February is short");
            Assert.That(template.SlotAt(2), Is.EqualTo(Utc(2026, 3, 31)),
                "and March is not - the schedule must not have kept the 28th");
            Assert.That(template.SlotAt(3), Is.EqualTo(Utc(2026, 4, 30)));
            Assert.That(template.SlotAt(4), Is.EqualTo(Utc(2026, 5, 31)));
        });
    }

    [Test]
    public void SlotAt_MonthlyIntoALeapFebruary_UsesTheTwentyNinth()
    {
        var template = MakeTemplate(Utc(2024, 1, 31));

        Assert.That(template.SlotAt(1), Is.EqualTo(Utc(2024, 2, 29)));
    }

    [Test]
    public void SlotAt_YearlyFromALeapDay_ClampsInCommonYearsOnly()
    {
        var template = MakeTemplate(Utc(2024, 2, 29), RecurrenceUnit.Year);

        Assert.Multiple(() =>
        {
            Assert.That(template.SlotAt(1), Is.EqualTo(Utc(2025, 2, 28)));
            Assert.That(template.SlotAt(4), Is.EqualTo(Utc(2028, 2, 29)),
                "the leap day comes back rather than being lost to the first clamp");
        });
    }

    [Test]
    public void SlotAt_WeeklyAndDaily_StepInFlatDays()
    {
        var weekly = MakeTemplate(Utc(2026, 1, 1), RecurrenceUnit.Week, interval: 2);
        var daily = MakeTemplate(Utc(2026, 1, 1), RecurrenceUnit.Day, interval: 3);

        Assert.Multiple(() =>
        {
            Assert.That(weekly.SlotAt(1), Is.EqualTo(Utc(2026, 1, 15)));
            Assert.That(daily.SlotAt(4), Is.EqualTo(Utc(2026, 1, 13)));
        });
    }

    [Test]
    public void SlotAt_IndexZero_IsTheAnchor()
    {
        var anchor = Utc(2026, 1, 1);

        Assert.That(MakeTemplate(anchor).SlotAt(0), Is.EqualTo(anchor));
    }

    // ══════════════════════════════════════════════════════════════════════════ Advancing
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void AdvanceFrom_TheAnchor_IsTheNextSlot()
    {
        var anchor = Utc(2026, 1, 1);

        Assert.That(MakeTemplate(anchor).AdvanceFrom(anchor), Is.EqualTo(Utc(2026, 2, 1)));
    }

    /// <summary>Paying four days late must not move rent day for every month after it, the same
    /// reason Chore.AdvanceFrom steps from the anchor rather than from when the work happened.</summary>
    [Test]
    public void AdvanceFrom_LatePayment_DoesNotDriftTheSchedule()
    {
        var template = MakeTemplate(Utc(2026, 1, 1));

        Assert.That(template.AdvanceFrom(Utc(2026, 1, 5)), Is.EqualTo(Utc(2026, 2, 1)));
    }

    [Test]
    public void AdvanceFrom_BeforeTheAnchor_IsTheAnchorItself()
    {
        var anchor = Utc(2026, 6, 1);

        Assert.That(MakeTemplate(anchor).AdvanceFrom(Utc(2026, 1, 1)), Is.EqualTo(anchor));
    }

    [Test]
    public void AdvanceFrom_ALongGap_StaysOnTheAnchorsCadence()
    {
        var template = MakeTemplate(Utc(2026, 1, 15));

        var next = template.AdvanceFrom(Utc(2026, 9, 20));

        Assert.That(next, Is.EqualTo(Utc(2026, 10, 15)),
            "still the 15th, rather than resetting to a month from 'now'");
    }

    // ══════════════════════════════════════════════════════════════════════════ Backlog collapse
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The behaviour that keeps a schedule entered six months after the house started
    /// paying it from emitting six past-dated bills - which, with AutoPost set, would be six charges
    /// in balances people settle in their bank accounts.</summary>
    [Test]
    public void FastForwardTo_ASixMonthBacklog_LandsOnTheCurrentPeriodOnly()
    {
        var template = MakeTemplate(Utc(2026, 1, 1));

        var skipped = template.FastForwardTo(Utc(2026, 7, 20));

        Assert.Multiple(() =>
        {
            Assert.That(skipped, Is.EqualTo(6), "January through June were never going to be paid here");
            Assert.That(template.NextDueAt, Is.EqualTo(Utc(2026, 7, 1)),
                "and the surviving slot is this month's, still on the anchor's day");
        });
    }

    [Test]
    public void FastForwardTo_AnUpToDateSchedule_DoesNothing()
    {
        var template = MakeTemplate(Utc(2026, 7, 1));

        var skipped = template.FastForwardTo(Utc(2026, 7, 3));

        Assert.Multiple(() =>
        {
            Assert.That(skipped, Is.Zero);
            Assert.That(template.NextDueAt, Is.EqualTo(Utc(2026, 7, 1)));
        });
    }

    [Test]
    public void FastForwardTo_AFutureAnchor_IsNeverPulledBackwards()
    {
        var anchor = Utc(2026, 9, 1);
        var template = MakeTemplate(anchor);

        var skipped = template.FastForwardTo(Utc(2026, 7, 20));

        Assert.Multiple(() =>
        {
            Assert.That(skipped, Is.Zero);
            Assert.That(template.NextDueAt, Is.EqualTo(anchor));
        });
    }

    [Test]
    public void FastForwardTo_IsBounded()
    {
        // A daily schedule anchored at the epoch has to terminate rather than spin, the same guard
        // Chore carries.
        var template = MakeTemplate(DateTimeOffset.UnixEpoch, RecurrenceUnit.Day);

        Assert.That(template.FastForwardTo(DateTimeOffset.UtcNow), Is.LessThanOrEqualTo(4096));
    }

    // ══════════════════════════════════════════════════════════════════════════ Interval bounds
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void IsIntervalValid_AcceptsTheOrdinaryCadences()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Month, 1), Is.True);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Month, 3), Is.True, "quarterly");
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Week, 2), Is.True);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Year, 1), Is.True);
        });
    }

    [Test]
    public void IsIntervalValid_RejectsWhatCannotBeMeant()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Month, 0), Is.False);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Month, -1), Is.False);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Month, 25), Is.False,
                "two years between bills is a typo in every house that has ever existed");
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Week, 53), Is.False);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Day, 366), Is.False);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Year, 6), Is.False);
        });
    }

    [Test]
    public void IsIntervalValid_BoundsAreInclusive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Day, 365), Is.True);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Week, 52), Is.True);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Month, 24), Is.True);
            Assert.That(RecurringExpense.IsIntervalValid(RecurrenceUnit.Year, 5), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Occurrences
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_CopiesAFixedAmountAndLeavesAVariableOneOpen()
    {
        var fixedBill = BillOccurrence.Create(MakeTemplate(Utc(2026, 1, 1)), Utc(2026, 1, 1));
        var variableBill = BillOccurrence.Create(
            MakeTemplate(Utc(2026, 1, 1), amountMinor: null), Utc(2026, 1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(fixedBill.AmountMinor, Is.EqualTo(85000));
            Assert.That(fixedBill.NeedsAmount(), Is.False);

            Assert.That(variableBill.AmountMinor, Is.Null);
            Assert.That(variableBill.NeedsAmount(), Is.True,
                "the normal state of an electricity bill until the letter arrives");
        });
    }

    [Test]
    public void NeedsAmount_IsOnlyEverTrueWhilePending()
    {
        var bill = BillOccurrence.Create(MakeTemplate(Utc(2026, 1, 1), amountMinor: null), Utc(2026, 1, 1));
        bill.Status = BillStatus.Skipped;

        Assert.That(bill.NeedsAmount(), Is.False,
            "a skipped period is not waiting on anybody for a figure");
    }

    /// <summary>The other half of the at-most-once guard.</summary>
    [Test]
    public void Reschedule_ReleasesTheAnnouncementStamp()
    {
        var bill = BillOccurrence.Create(MakeTemplate(Utc(2026, 1, 1)), Utc(2026, 1, 1));
        bill.RemindedAt = DateTimeOffset.UtcNow;

        bill.Reschedule(Utc(2026, 1, 15));

        Assert.Multiple(() =>
        {
            Assert.That(bill.DueAt, Is.EqualTo(Utc(2026, 1, 15)));
            Assert.That(bill.RemindedAt, Is.Null);
        });
    }

    [Test]
    public void Reschedule_ToTheSameDate_KeepsTheStamp()
    {
        var stamped = DateTimeOffset.UtcNow;
        var bill = BillOccurrence.Create(MakeTemplate(Utc(2026, 1, 1)), Utc(2026, 1, 1));
        bill.RemindedAt = stamped;

        bill.Reschedule(Utc(2026, 1, 1));

        Assert.That(bill.RemindedAt, Is.EqualTo(stamped),
            "a no-op edit must not turn into a second notification");
    }
}
