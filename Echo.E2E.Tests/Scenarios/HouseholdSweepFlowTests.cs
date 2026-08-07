using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// The asynchronous half of the household modules: everything <c>HouseholdReconcileService</c> does
/// on its own, which is most of what these modules actually notify people about.
/// </summary>
[TestFixture]
[Category("E2E")]
public class HouseholdSweepFlowTests
{
    /// <summary>Generous against a two-second sweep: the timer is the fast part, and the slow part
    /// is a loaded machine running eight service processes and a container set. A tight value here
    /// buys nothing and turns load into a failure.</summary>
    private static readonly TimeSpan AlertTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Long enough for several sweep passes to have come and gone, which is what makes
    /// "and not again" an assertion rather than a coincidence.</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(12);

    private EchoTestStack _stack = null!;

    private string _ownerId = null!;
    private HttpClient _owner = null!;

    /// <summary>A second member holding only <c>@everyone</c>.</summary>
    private string _flatmateId = null!;
    private HttpClient _flatmate = null!;

    private HouseholdAlertSpy _alerts = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "sweep", "sweep-test-instance");

        var (ownerId, ownerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "sweepowner");
        _ownerId = ownerId;
        _owner = AuthedClient(ownerToken);

        var (flatmateId, flatmateToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "sweepflatmate");
        _flatmateId = flatmateId;
        _flatmate = AuthedClient(flatmateToken);

        _alerts = await HouseholdAlertSpy.StartAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_alerts is not null) await _alerts.DisposeAsync();

        _owner?.Dispose();
        _flatmate?.Dispose();

        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    // ── Bills ────────────────────────────────────────────────────────────────

    /// <summary>A bill that posts itself.</summary>
    [Test]
    public async Task AutoPostingBill_ChargesTheHouseWithoutAnybodyPressingAnything()
    {
        var house = await CreateHouseholdAsync("Auto Flat", withFlatmate: true);
        var ledger = house.ChannelId("ledger");

        // Odd on purpose: 3751 + 3750, so a split that lost the remainder would show up here.
        const long totalMinor = 7501;

        var template = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/recurring-expenses", new
            {
                Description = "Standing order that pays itself",
                AmountMinor = totalMinor,
                RecurrenceUnit = "Month",
                AnchorAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LeadDays = 0,
                AutoPost = true,
                SplitKind = "Equal",
                Category = "Utilities",
            }),
            "Create the auto-posting schedule");

        Assert.That(template.GetProperty("autoPost").GetBoolean(), Is.True);

        // ledger.bill_posted carries the EXPENSE id, not the bill's - its two siblings carry the
        // occurrence.
        var postedAlert = await _alerts.WaitForAsync(
            a => a.Kind == "ledger.bill_posted" && a.GuildId == house.GuildId,
            AlertTimeout, "ledger.bill_posted for the auto-posted bill");

        var bill = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{ledger}/bills"), "Re-list bills"))
            .EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            Assert.That(bill.GetProperty("status").GetString(), Is.EqualTo("Posted"),
                "the sweep, not a person, posted this");
            Assert.That(bill.GetProperty("postedByUserId").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "an auto-post has no actor - nobody pressed anything");
            Assert.That(postedAlert.TargetId, Is.EqualTo(bill.GetProperty("expenseId").GetString()),
                "ledger.bill_posted deep-links to the expense that now moves the balance, not to "
                + "the schedule entry that produced it");
        });

        var expenseId = bill.GetProperty("expenseId").GetString()!;

        var expense = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{ledger}/expenses"), "List expenses"))
            .GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == expenseId);

        var shares = expense.GetProperty("shares").EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(expense.GetProperty("amountMinor").GetInt64(), Is.EqualTo(totalMinor));
            Assert.That(expense.GetProperty("category").GetString(), Is.EqualTo("Utilities"));
            Assert.That(shares.Sum(s => s.GetProperty("amountMinor").GetInt64()), Is.EqualTo(totalMinor),
                "an auto-posted bill divides exactly like a hand-entered one - same splitter, same "
                + "integer arithmetic, down to which flatmate absorbs the odd rappen");
            Assert.That(shares.Select(s => s.GetProperty("amountMinor").GetInt64()),
                Is.EquivalentTo(new long[] { 3751, 3750 }));
        });

        // Everyone with a share is told, and the actor is excluded - but the sweep has no actor, so
        // here that means both of them.
        Assert.That(_alerts.RecipientsOf("ledger.bill_posted", expenseId),
            Is.EquivalentTo(new[] { _ownerId, _flatmateId }),
            "both flatmates' balances just moved without either of them doing anything");

        // And it does not do it twice.
        await Task.Delay(QuietWindow);

        var expensesLater = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/expenses"), "Re-list expenses");

        Assert.Multiple(() =>
        {
            Assert.That(expensesLater.GetProperty("items").GetArrayLength(), Is.EqualTo(1),
                "the sweep runs every couple of seconds and must post this bill exactly once");
            Assert.That(_alerts.For("ledger.bill_posted", expenseId), Has.Count.EqualTo(2),
                "one alert per recipient, and no repeats on later passes");
        });
    }

    /// <summary>
    /// A bill whose amount nobody knows yet asks the people who can answer, and posts nothing.
    /// </summary>
    [Test]
    public async Task VariableBill_AsksThePeopleWhoCanPriceIt_AndPostsNothing()
    {
        var house = await CreateHouseholdAsync("Meter Flat", withFlatmate: true);
        var ledger = house.ChannelId("ledger");

        // No AmountMinor at all: the normal state of an electricity bill until it arrives.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/recurring-expenses", new
            {
                Description = "Electricity",
                RecurrenceUnit = "Month",
                AnchorAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LeadDays = 0,
                SplitKind = "Equal",
            }),
            "Create the variable schedule");

        var billId = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{ledger}/bills"), "List bills"))
            .EnumerateArray().Single().GetProperty("id").GetString()!;

        await _alerts.WaitForAsync(
            a => a.Kind == "ledger.bill_needs_amount" && a.TargetId == billId,
            AlertTimeout, $"ledger.bill_needs_amount for {billId}");

        Assert.That(_alerts.RecipientsOf("ledger.bill_needs_amount", billId),
            Is.EqualTo(new[] { _ownerId }),
            "only members who can post the bill are told it needs an amount - the flatmate holds "
            + "@everyone, which carries AddExpenses but not ManageLedger");

        var bill = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{ledger}/bills"), "Re-list bills"))
            .EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            Assert.That(bill.GetProperty("status").GetString(), Is.EqualTo("Pending"),
                "a bill nobody has priced must not post itself");
            Assert.That(bill.GetProperty("needsAmount").GetBoolean(), Is.True);
            Assert.That(bill.GetProperty("amountMinor").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(bill.GetProperty("expenseId").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "nothing reached the ledger");
        });

        Assert.That(
            (await ReadJsonAsync(await _owner.GetAsync($"/api/v1/channels/{ledger}/expenses"), "List expenses"))
            .GetProperty("items").GetArrayLength(),
            Is.Zero, "a guessed amount is worse than no amount");

        // AutoPost cannot even be configured for one of these, which is the rule that makes the
        // above structural rather than incidental.
        var illegal = await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/recurring-expenses", new
        {
            Description = "Electricity, but posting itself",
            RecurrenceUnit = "Month",
            AutoPost = true,
        });

        await E2EAssert.HasStatusAsync(illegal, HttpStatusCode.BadRequest, _stack.Guild,
            "AutoPost without a fixed amount must be refused");
    }

    // ── Chores ───────────────────────────────────────────────────────────────

    /// <summary>The chore reminder, and the stamp that stops it becoming nagging.</summary>
    [Test]
    public async Task ChoreDue_RemindsTheAssigneeOnce_AndNotAgainOnEveryLaterPass()
    {
        var house = await CreateHouseholdAsync("Bins Flat", withFlatmate: true);
        var chores = house.ChannelId("chores");

        // Fixed to the flatmate rather than rotated, so "the assignee and only the assignee" is a
        // statement about somebody who is not also the guild owner.
        var chore = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{chores}/chores", new
            {
                Title = "Take the bins out",
                IntervalDays = 7,
                EffortMinutes = 10,
                FixedAssigneeUserId = _flatmateId,
                AnchorAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            }),
            "Create the chore");

        var occurrence = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{chores}/chores/occurrences"), "List occurrences"))
            .EnumerateArray().Single();

        var occurrenceId = occurrence.GetProperty("id").GetString()!;
        Assert.That(occurrence.GetProperty("assignedUserId").GetString(), Is.EqualTo(_flatmateId));

        await _alerts.WaitForAsync(
            a => a.Kind == "chore.due" && a.TargetId == occurrenceId,
            AlertTimeout, $"chore.due for {occurrenceId}");

        Assert.That(_alerts.RecipientsOf("chore.due", occurrenceId), Is.EqualTo(new[] { _flatmateId }),
            "a chore reminder goes to whoever has to do it, and to nobody else - the rest of the "
            + "house learning about it is what makes a rota feel like surveillance");

        Assert.That(
            await GuildDatabase.WaitForStampAsync(
                _stack, "chore_occurrences", "reminded_at", occurrenceId, AlertTimeout),
            Is.True, "reminded_at is what makes this at-most-once");

        // The real assertion. Many passes go by; none of them reminds again.
        await Task.Delay(QuietWindow);

        Assert.That(_alerts.For("chore.due", occurrenceId), Has.Count.EqualTo(1),
            "the stamp has to survive every later pass - a sweep this fast would otherwise buzz the "
            + "assignee every couple of seconds");
    }

    // ── Pantry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Expiry warnings: one per pantry rather than one per item, and each pantry on its own
    /// horizon.
    /// </summary>
    [Test]
    public async Task PantryExpiry_WarnsOncePerPantry_OnThatPantrysOwnHorizon()
    {
        var house = await CreateHouseholdAsync("Fridge Flat");
        var fridge = house.ChannelId("pantry");

        var freezer = (await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/guilds/{house.GuildId}/channels",
                    new { Name = "freezer", Description = "the cold one", Type = "Pantry" }),
                "Create the freezer"))
            .GetProperty("id").GetString()!;

        // One day, so nothing below is eligible yet whatever its date.
        foreach (var channel in new[] { fridge, freezer })
        {
            await ReadJsonAsync(
                await _owner.PutAsJsonAsync($"/api/v1/channels/{channel}/pantry/config",
                    new { ExpiryWarningDays = 1 }),
                "Park the horizon out of the way");
        }

        var now = DateTimeOffset.UtcNow;

        async Task<string> StockAsync(string channel, string name, int expiresInDays) =>
            (await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{channel}/pantry-items",
                    new { Name = name, Quantity = 1m, ExpiresAt = now.AddDays(expiresInDays) }),
                $"Stock {name}"))
            .GetProperty("id").GetString()!;

        var milk = await StockAsync(fridge, "Milk", 2);
        var yoghurt = await StockAsync(fridge, "Yoghurt", 3);
        var cheese = await StockAsync(fridge, "Cheese", 20);      // outside even the widened fridge horizon
        var peas = await StockAsync(freezer, "Peas", 10);         // outside a fridge horizon, inside a freezer one

        // Nothing has been warned about yet: every item is beyond a one-day horizon.
        Assert.That(
            await _alerts.NoneArrivedAsync(a => a.GuildId == house.GuildId && a.Kind == "pantry.expiring", QuietWindow),
            Is.True, "an item outside its pantry's horizon is not news yet");

        // The single write that makes the fridge's two eligible at once, and neither of the others.
        await ReadJsonAsync(
            await _owner.PutAsJsonAsync($"/api/v1/channels/{fridge}/pantry/config",
                new { ExpiryWarningDays = 5 }),
            "Widen the fridge to five days");

        await _alerts.WaitForAsync(
            a => a.Kind == "pantry.expiring" && a.TargetId == fridge,
            AlertTimeout, $"pantry.expiring for the fridge ({fridge})");

        Assert.Multiple(() =>
        {
            Assert.That(_alerts.For("pantry.expiring", fridge), Has.Count.EqualTo(1),
                "one alert for the pantry, not one per item - the body names the rest");
            Assert.That(_alerts.For("pantry.expiring", freezer), Is.Empty,
                "the freezer is still on a one-day horizon and has nothing inside it");
        });

        // Now the freezer, on a horizon a flat three days would never have reached.
        await ReadJsonAsync(
            await _owner.PutAsJsonAsync($"/api/v1/channels/{freezer}/pantry/config",
                new { ExpiryWarningDays = 14 }),
            "Widen the freezer to a fortnight");

        await _alerts.WaitForAsync(
            a => a.Kind == "pantry.expiring" && a.TargetId == freezer,
            AlertTimeout, $"pantry.expiring for the freezer ({freezer})");

        Assert.That(_alerts.For("pantry.expiring", freezer), Has.Count.EqualTo(1),
            "each pantry warns on its own horizon, which is the whole reason the setting is "
            + "per-channel rather than per-guild");

        // The stamps say which items were covered, which is the part "one alert, several items"
        // would otherwise leave unproven.
        var milkStamped = await WaitForStampAsync(milk);
        var yoghurtStamped = await WaitForStampAsync(yoghurt);
        var peasStamped = await WaitForStampAsync(peas);

        // Read once, and only now: the commit those three waited for is the same one that would
        // have carried this item, so an unstamped read here is a real absence rather than an early
        // one.
        var cheeseStamped = await StampedAsync("pantry_items", "expiry_notified_at", cheese);

        // Read before asserting rather than inside an Assert.Multiple: the block takes a synchronous
        // delegate, so an async one is an async void whose assertions land after the block has
        // already closed - it passes whatever they say.
        Assert.Multiple(() =>
        {
            Assert.That(milkStamped, Is.True, "milk was two days out and inside the widened horizon");
            Assert.That(yoghurtStamped, Is.True, "yoghurt was three days out");
            Assert.That(peasStamped, Is.True, "the peas were ten days out and inside a fortnight");
            Assert.That(cheeseStamped, Is.False,
                "twenty days out is beyond even the widened fridge horizon and must stay untouched");
        });

        Task<bool> WaitForStampAsync(string itemId) => GuildDatabase.WaitForStampAsync(
            _stack, "pantry_items", "expiry_notified_at", itemId, AlertTimeout);
    }

    // ── Meals ────────────────────────────────────────────────────────────────

    /// <summary>The cooking reminder reaches the named cook and nobody else.</summary>
    [Test]
    public async Task CookingToday_ReachesTheNamedCook_AndNobodyElse()
    {
        var house = await CreateHouseholdAsync("Dinner Flat", withFlatmate: true);
        var meals = house.ChannelId("meals");

        // Quiet hours off, so nothing is deferred - this is here purely to give the guild a time
        // zone, which is the only one MealAlertService can read.
        await ReadJsonAsync(
            await _owner.PutAsJsonAsync($"/api/v1/guilds/{house.GuildId}/quiet-hours", new
            {
                Enabled = false,
                StartMinuteLocal = 22 * 60,
                EndMinuteLocal = 7 * 60,
                TimeZoneId = ZoneWhereItIsMidMorningNow(),
            }),
            "Give the house a time zone");

        var entry = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{meals}/meal-plan", new
            {
                Date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                Slot = "Dinner",
                FreeText = "Thai curry",
                CookUserId = _flatmateId,
            }),
            "Put the flatmate down to cook");

        var entryId = entry.GetProperty("id").GetString()!;

        await _alerts.WaitForAsync(
            a => a.Kind == "meals.cooking_today" && a.TargetId == entryId,
            AlertTimeout, $"meals.cooking_today for {entryId}");

        Assert.That(_alerts.RecipientsOf("meals.cooking_today", entryId), Is.EqualTo(new[] { _flatmateId }),
            "the cook, and nobody else");

        await Task.Delay(QuietWindow);

        Assert.That(_alerts.For("meals.cooking_today", entryId), Has.Count.EqualTo(1),
            "at most once per entry");
    }

    // ── Maintenance ──────────────────────────────────────────────────────────

    /// <summary>
    /// A service falling due and a warranty running out both warn - and a warranty that has already
    /// run out does not.
    /// </summary>
    [Test]
    public async Task Maintenance_WarnsAboutAServiceAndALapsingWarranty_ButNotOneAlreadyGone()
    {
        var house = await CreateHouseholdAsync("Boiler Flat");
        var upkeep = house.ChannelId("upkeep");

        var now = DateTimeOffset.UtcNow;

        async Task<string> CatalogueAsync(string name, object body) =>
            (await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{upkeep}/maintenance-assets", body),
                $"Catalogue {name}"))
            .GetProperty("id").GetString()!;

        // Serviced 40 days ago on a 30-day interval: due 10 days ago, comfortably inside the
        // 30-day lateness cutoff.
        var boiler = await CatalogueAsync("the boiler", new
        {
            Name = "Boiler",
            ServiceIntervalDays = 30,
            LastServicedAt = now.AddDays(-40),
        });

        // Ten days left, so inside the 30-day warning horizon.
        var washer = await CatalogueAsync("the washing machine", new
        {
            Name = "Washing machine",
            WarrantyUntil = now.AddDays(10),
        });

        // Gone a fortnight ago. There is nothing left to do about it and nothing honest to say.
        var dryer = await CatalogueAsync("the tumble dryer", new
        {
            Name = "Tumble dryer",
            WarrantyUntil = now.AddDays(-14),
        });

        await _alerts.WaitForAsync(
            a => a.Kind == "maintenance.due" && a.TargetId == boiler,
            AlertTimeout, $"maintenance.due for the boiler ({boiler})");

        await _alerts.WaitForAsync(
            a => a.Kind == "maintenance.warranty" && a.TargetId == washer,
            AlertTimeout, $"maintenance.warranty for the washing machine ({washer})");

        // All three kinds point at the asset, which is where all three questions are answered -
        // unlike the ledger.bill_* family, where one of the three points somewhere else.
        Assert.Multiple(() =>
        {
            Assert.That(_alerts.For("maintenance.due", boiler), Is.Not.Empty);
            Assert.That(_alerts.For("maintenance.warranty", washer), Is.Not.Empty);
        });

        // Silent, but dealt with: the row is stamped so it stops being re-examined on every pass
        // for the rest of time.
        Assert.That(
            await GuildDatabase.WaitForStampAsync(
                _stack, "maintenance_assets", "warranty_notified_at", dryer, AlertTimeout),
            Is.True, "the lapsed warranty is retired rather than left in the candidate set");

        // Asserted after the stamp, and after a settle: the alert would have been dispatched before
        // the stamp was committed, so this is an absence measured at a point where a present one
        // would already have arrived.
        await Task.Delay(QuietWindow);

        Assert.That(_alerts.For("maintenance.warranty", dryer), Is.Empty,
            "a warranty that lapsed a fortnight ago produces nothing - the copy says it runs out "
            + "in so many days, which is not a true sentence about one that already has");
    }

    // ── The lateness cutoffs ─────────────────────────────────────────────────

    /// <summary>
    /// Nothing fires for anything too late to be worth mentioning - and every such row is still
    /// retired.
    /// </summary>
    [Test]
    public async Task NothingTooLateToBeUseful_IsEverSent_ThoughEveryRowIsRetired()
    {
        var house = await CreateHouseholdAsync("Outage Flat");
        var now = DateTimeOffset.UtcNow;

        // A chore due a day ago, against a twelve-hour cutoff.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("chores")}/chores", new
            {
                Title = "Something nobody did",
                IntervalDays = 7,
                EffortMinutes = 10,
                GraceHours = 0,
                FixedAssigneeUserId = _ownerId,
                AnchorAt = now.AddHours(-24),
            }),
            "Create the long-overdue chore");

        var staleChore = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{house.ChannelId("chores")}/chores/occurrences"),
                "List occurrences"))
            .EnumerateArray().Single().GetProperty("id").GetString()!;

        // A bill due ten days ago, against a seven-day cutoff.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync(
                $"/api/v1/channels/{house.ChannelId("ledger")}/recurring-expenses", new
                {
                    Description = "Rent nobody announced",
                    AmountMinor = 5000,
                    RecurrenceUnit = "Month",
                    AnchorAt = now.AddDays(-10),
                    LeadDays = 0,
                }),
            "Create the long-overdue schedule");

        var staleBill = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{house.ChannelId("ledger")}/bills"), "List bills"))
            .EnumerateArray().Single().GetProperty("id").GetString()!;

        // Something that went off ten days ago, against a seven-day cutoff.
        var staleItem = (await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("pantry")}/pantry-items",
                    new { Name = "Forgotten yoghurt", Quantity = 1m, ExpiresAt = now.AddDays(-10) }),
                "Stock the forgotten yoghurt"))
            .GetProperty("id").GetString()!;

        // A service due forty days ago, against a thirty-day cutoff.
        var staleAsset = (await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("upkeep")}/maintenance-assets",
                    new { Name = "Long-neglected boiler", ServiceIntervalDays = 30, LastServicedAt = now.AddDays(-70) }),
                "Catalogue the neglected boiler"))
            .GetProperty("id").GetString()!;

        // Every one of them is retired, which is what proves the sweep looked at them at all - an
        // absence of alerts on its own would also be what a sweep that never ran looks like.
        var choreRetired = await GuildDatabase.WaitForStampAsync(
            _stack, "chore_occurrences", "reminded_at", staleChore, AlertTimeout);
        var billRetired = await GuildDatabase.WaitForStampAsync(
            _stack, "bill_occurrences", "reminded_at", staleBill, AlertTimeout);
        var itemRetired = await GuildDatabase.WaitForStampAsync(
            _stack, "pantry_items", "expiry_notified_at", staleItem, AlertTimeout);
        var assetRetired = await GuildDatabase.WaitForStampAsync(
            _stack, "maintenance_assets", "service_notified_at", staleAsset, AlertTimeout);

        Assert.Multiple(() =>
        {
            Assert.That(choreRetired, Is.True, "the stale chore occurrence is retired");
            Assert.That(billRetired, Is.True, "the stale bill is retired");
            Assert.That(itemRetired, Is.True, "the composted yoghurt is retired");
            Assert.That(assetRetired, Is.True, "the neglected boiler is retired");
        });

        // Every one of these services dispatches its alert before committing the stamp, so seeing
        // the stamp already means any alert has been published.
        await Task.Delay(QuietWindow);

        // And having been looked at, none of them was announced.
        Assert.Multiple(() =>
        {
            Assert.That(_alerts.For("chore.due", staleChore), Is.Empty,
                "a chore more than twelve hours overdue is just overdue, and the board already says so");
            Assert.That(_alerts.For("ledger.bill_due", staleBill), Is.Empty,
                "a bill more than a week late is late rather than news");
            Assert.That(_alerts.For("pantry.expiring", house.ChannelId("pantry")), Is.Empty,
                "nothing warns about something a week past its date");
            Assert.That(_alerts.For("maintenance.due", staleAsset), Is.Empty,
                "a service more than a month overdue has stopped being a reminder");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record Household(string GuildId, IReadOnlyDictionary<string, string> ChannelIds)
    {
        public string ChannelId(string name) => ChannelIds[name];
    }

    private async Task<Household> CreateHouseholdAsync(string name, bool withFlatmate = false)
    {
        var created = await ReadJsonAsync(
            await _owner.PostAsJsonAsync("/api/v1/guilds", new { Name = name, Kind = "Household" }),
            "Create a household guild");

        var guildId = created.GetProperty("id").GetString()!;

        if (withFlatmate)
        {
            var invite = await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "Permanent" }),
                "Create an invite");

            var redeem = await _flatmate.PostAsync(
                $"/api/v1/invites/{invite.GetProperty("id").GetString()}/redeem", null);
            await E2EAssert.SucceededAsync(redeem, _stack.Guild, "The flatmate could not move in");
        }

        var guild = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/guilds/{guildId}"), "Read the household back");

        return new Household(guildId, guild.GetProperty("channels").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetString()!));
    }

    /// <summary>
    /// A time zone in which the current instant sits in the middle of the window a cooking reminder
    /// can be sent in.
    /// </summary>
    private static string ZoneWhereItIsMidMorningNow()
    {
        var now = DateTimeOffset.UtcNow;
        var todayMorningLocal = DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(new TimeOnly(8, 0));

        string? best = null;
        var bestDistance = TimeSpan.MaxValue;

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            // MealAlertService.MorningInstant, exactly: 08:00 on the entry's date, in this zone.
            var fireAt = new DateTimeOffset(todayMorningLocal, zone.GetUtcOffset(todayMorningLocal))
                .ToUniversalTime();

            var lateness = now - fireAt;
            if (lateness < TimeSpan.Zero || lateness > TimeSpan.FromHours(12)) continue;

            // Six hours late is the middle of a twelve-hour window.
            var distance = (lateness - TimeSpan.FromHours(6)).Duration();
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = zone.Id;
        }

        Assert.That(best, Is.Not.Null,
            "no installed time zone puts the current instant inside the cooking-reminder window, "
            + "which should be impossible - the window is twelve hours wide and zone offsets span "
            + "more than a day");

        return best!;
    }

    private Task<bool> StampedAsync(string table, string column, string id) =>
        GuildDatabase.IsStampedAsync(_stack, table, column, id);

    private HttpClient AuthedClient(string token)
    {
        var client = new HttpClient { BaseAddress = _stack.Guild.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string what)
    {
        Assert.That(response.IsSuccessStatusCode, Is.True,
            $"{what} failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
