using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;
using Guild.Domain.Enums;
using Guild.Domain.Services;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// The household modules against a live stack, and above all against a live Postgres.
/// </summary>
[TestFixture]
[Category("E2E")]
public class HouseholdFlowTests
{
    private EchoTestStack _stack = null!;

    private string _ownerId = null!;
    private HttpClient _owner = null!;

    /// <summary>A second real member, so an Equal split has something to divide across and a
    /// balance that sums to zero is an assertion rather than a tautology.</summary>
    private string _flatmateId = null!;
    private HttpClient _flatmate = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "household", "household-test-instance");

        var (ownerId, ownerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "houseowner");
        _ownerId = ownerId;
        _owner = AuthedClient(_stack.Guild, ownerToken);

        var (flatmateId, flatmateToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "houseflatmate");
        _flatmateId = flatmateId;
        _flatmate = AuthedClient(_stack.Guild, flatmateToken);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        _owner?.Dispose();
        _flatmate?.Dispose();

        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    [Test]
    public async Task HouseholdGuild_SeedsItsChannelTree_TheFlatmatesRole_AndTheHouseManual()
    {
        var house = await CreateHouseholdAsync("Seeded Flat");

        Assert.Multiple(() =>
        {
            Assert.That(house.CategoryNames, Is.EquivalentTo(new[] { "Home", "House", "Voice" }),
                "a household is seeded with three categories, not the usual Text/Voice pair");

            // One channel per module, so nothing is hidden behind a settings tour.
            Assert.That(
                house.ChannelTypes.Select(c => $"{c.Key}:{c.Value}"),
                Is.EquivalentTo(new[]
                {
                    "general:Text", "groceries:List", "chores:Chores", "meals:Meals",
                    "pantry:Pantry", "ledger:Ledger", "decisions:Decisions", "upkeep:Maintenance",
                    "house:Voice",
                }));
        });

        var flatmates = house.Raw.GetProperty("roles").EnumerateArray()
            .SingleOrDefault(r => r.GetProperty("name").GetString() == "Flatmates");

        Assert.That(flatmates.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "a household is seeded with a Flatmates role - it is the default chore rotation pool, "
            + "so without it the only pool a chore can rotate over is every member, guests included");
        Assert.That(flatmates.GetProperty("position").GetInt32(), Is.EqualTo(1),
            "Flatmates sits above @everyone so a guest can never manage a flatmate");

        var wiki = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/guilds/{house.GuildId}/wiki"), "Read the wiki");

        var categoryNames = wiki.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!)
            .ToList();
        Assert.That(categoryNames, Does.Contain(HouseManualSeed.CategoryName));

        var pages = wiki.GetProperty("pages").EnumerateArray().ToList();
        var pageTitles = pages.Select(p => p.GetProperty("title").GetString()!).ToList();

        Assert.That(pageTitles, Is.EquivalentTo(HouseManualSeed.PageTitles),
            "the starter house manual is the difference between guest access being a permission and "
            + "being useful");

        var pinned = pages.Where(p => p.GetProperty("isPinned").GetBoolean())
            .Select(p => p.GetProperty("title").GetString()!)
            .ToList();
        Assert.That(pinned, Is.EqualTo(new[] { "How this house works" }),
            "the map page is pinned; the other five are reference");
    }

    // ── The enums, which are why this fixture was written ────────────────────

    /// <summary>
    /// Every C# member of every household-mapped enum exists as a label on its Postgres type.
    /// </summary>
    [Test]
    public async Task EveryHouseholdEnumMember_ExistsAsAPostgresLabel()
    {
        await AssertFullyMigratedAsync<RecurrenceUnit>("recurrence_unit");
        await AssertFullyMigratedAsync<BillStatus>("bill_status");
        await AssertFullyMigratedAsync<ExpenseCategory>("expense_category");
        await AssertFullyMigratedAsync<MealSlot>("meal_slot");
        await AssertFullyMigratedAsync<AssetStatus>("asset_status");

        // The two that were appended to rather than created.
        await AssertFullyMigratedAsync<ChannelType>("channel_type");
        await AssertFullyMigratedAsync<AuditActionType>("audit_action_type");
    }

    /// <summary>
    /// The other half: drive every member of the five new enums through a real HTTP write and read
    /// it back, so a label that exists but is mapped to the wrong name, or a column typed against
    /// the wrong enum, fails here rather than in production.
    /// </summary>
    [Test]
    public async Task EveryNewEnumMember_SurvivesARealWriteAndReadBack()
    {
        var house = await CreateHouseholdAsync("Enum Flat");

        // ── channel_type: the two appended members, created rather than only seeded ──

        var extraMeals = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/guilds/{house.GuildId}/channels",
                new { Name = "sunday-lunch", Description = "the other meals board", Type = "Meals" }),
            "Create a second Meals channel");

        var extraUpkeep = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/guilds/{house.GuildId}/channels",
                new { Name = "cellar", Description = "the other upkeep board", Type = "Maintenance" }),
            "Create a second Maintenance channel");

        Assert.Multiple(() =>
        {
            Assert.That(extraMeals.GetProperty("type").GetString(), Is.EqualTo("Meals"));
            Assert.That(extraUpkeep.GetProperty("type").GetString(), Is.EqualTo("Maintenance"));
        });

        // ── recurrence_unit ──────────────────────────────────────────────────

        var ledger = house.ChannelId("ledger");

        // LeadDays 0, and anchored a minute ago rather than exactly now.
        foreach (var unit in Enum.GetValues<RecurrenceUnit>())
        {
            await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/recurring-expenses", new
                {
                    Description = $"Standing order every {unit}",
                    AmountMinor = 1200,
                    RecurrenceUnit = unit.ToString(),
                    RecurrenceInterval = 1,
                    AnchorAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    LeadDays = 0,
                    SplitKind = "Equal",
                }),
                $"Create a {unit} schedule");
        }

        var templates = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/recurring-expenses"), "List schedules");

        Assert.That(
            templates.EnumerateArray().Select(t => t.GetProperty("recurrenceUnit").GetString()!).ToHashSet(),
            Is.EquivalentTo(Enum.GetNames<RecurrenceUnit>()),
            "every RecurrenceUnit must survive the round trip through the recurrence_unit column");

        // ── bill_status ──────────────────────────────────────────────────────

        var pendingBills = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{ledger}/bills?status=Pending"), "List pending bills"))
            .EnumerateArray().ToList();

        Assert.That(pendingBills, Has.Count.EqualTo(4),
            "a schedule anchored at now generates its first bill on the create call, not on the sweep");

        var postResponse = await _owner.PostAsJsonAsync(
            $"/api/v1/bills/{pendingBills[0].GetProperty("id").GetString()}/post", new { });
        await E2EAssert.SucceededAsync(postResponse, _stack.Guild, "Post a bill failed");

        var skipResponse = await _owner.PostAsJsonAsync(
            $"/api/v1/bills/{pendingBills[1].GetProperty("id").GetString()}/skip",
            new { Reason = "the flat was empty" });
        await E2EAssert.HasStatusAsync(skipResponse, HttpStatusCode.NoContent, _stack.Guild, "Skip a bill failed");

        var allBills = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/bills"), "List every bill");

        Assert.That(
            allBills.EnumerateArray().Select(b => b.GetProperty("status").GetString()!).ToHashSet(),
            Is.EquivalentTo(Enum.GetNames<BillStatus>()),
            "every BillStatus must survive the round trip through the bill_status column");

        // ── expense_category ─────────────────────────────────────────────────

        foreach (var category in Enum.GetValues<ExpenseCategory>())
        {
            await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/expenses", new
                {
                    Description = $"Something {category}",
                    AmountMinor = 1300,
                    Category = category.ToString(),
                    SplitKind = "Equal",
                }),
                $"Create a {category} expense");
        }

        var expenses = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/expenses?limit=200"), "List expenses");

        Assert.That(
            expenses.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("category").GetString()!).ToHashSet(),
            Is.SupersetOf(Enum.GetNames<ExpenseCategory>()),
            "every ExpenseCategory must survive the round trip through the expense_category column");

        // ── meal_slot ────────────────────────────────────────────────────────

        var meals = house.ChannelId("meals");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var slot in Enum.GetValues<MealSlot>())
        {
            await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{meals}/meal-plan", new
                {
                    Date = today.ToString("yyyy-MM-dd"),
                    Slot = slot.ToString(),
                    FreeText = $"leftovers ({slot})",
                }),
                $"Plan a {slot}");
        }

        var plan = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{meals}/meal-plan?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}"),
            "Read the meal plan");

        Assert.That(
            plan.EnumerateArray().Select(e => e.GetProperty("slot").GetString()!).ToHashSet(),
            Is.EquivalentTo(Enum.GetNames<MealSlot>()),
            "every MealSlot must survive the round trip through the meal_slot column");

        // ── asset_status ─────────────────────────────────────────────────────

        var upkeep = house.ChannelId("upkeep");

        foreach (var status in Enum.GetValues<AssetStatus>())
        {
            var asset = await ReadJsonAsync(
                await _owner.PostAsJsonAsync($"/api/v1/channels/{upkeep}/maintenance-assets",
                    new { Name = $"Machine that is {status}" }),
                $"Catalogue the {status} machine");

            var statusResponse = await _owner.PutAsJsonAsync(
                $"/api/v1/maintenance-assets/{asset.GetProperty("id").GetString()}/status",
                new { Status = status.ToString(), Note = "found this morning" });
            await E2EAssert.SucceededAsync(statusResponse, _stack.Guild, $"Set status {status} failed");
        }

        var assets = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{upkeep}/maintenance-assets"), "List assets");

        Assert.That(
            assets.EnumerateArray().Select(a => a.GetProperty("status").GetString()!).ToHashSet(),
            Is.EquivalentTo(Enum.GetNames<AssetStatus>()),
            "every AssetStatus must survive the round trip through the asset_status column");

        // ── audit_action_type: the nine members this wave appended ───────────

        var doomedTemplate = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/recurring-expenses", new
            {
                Description = "A standing order the house cancels",
                AmountMinor = 900,
                RecurrenceUnit = "Month",
                AnchorAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LeadDays = 0,
            }),
            "Create the schedule that gets cancelled");
        var doomedTemplateId = doomedTemplate.GetProperty("id").GetString()!;

        await ReadJsonAsync(
            await _owner.PatchAsJsonAsync($"/api/v1/recurring-expenses/{doomedTemplateId}",
                new { Description = "Renamed before cancelling" }),
            "Rename the schedule");

        var deleteTemplate = await _owner.DeleteAsync($"/api/v1/recurring-expenses/{doomedTemplateId}");
        await E2EAssert.HasStatusAsync(
            deleteTemplate, HttpStatusCode.NoContent, _stack.Guild, "Cancel the schedule failed");

        var doomedAsset = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{upkeep}/maintenance-assets",
                new { Name = "A machine the house no longer has" }),
            "Catalogue the machine that leaves");

        var deleteAsset = await _owner.DeleteAsync(
            $"/api/v1/maintenance-assets/{doomedAsset.GetProperty("id").GetString()}");
        await E2EAssert.HasStatusAsync(
            deleteAsset, HttpStatusCode.NoContent, _stack.Guild, "Remove the machine failed");

        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{upkeep}/maintenance-records",
                new { Title = "Bled the radiators", Description = "took an hour" }),
            "Log a repair");

        var actions = await GuildDatabase.AuditActionsAsync(_stack, house.GuildId);

        Assert.That(actions, Is.SupersetOf(new[]
        {
            "recurring_expense_created", "recurring_expense_updated", "recurring_expense_deleted",
            "bill_posted", "bill_skipped",
            "maintenance_asset_created", "maintenance_asset_updated", "maintenance_asset_deleted",
            "maintenance_record_created",
        }), "every audit action this wave appended must be writable to the audit_action_type column");
    }

    // ── Bills ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A bill from schedule to ledger row: the template, the occurrence it generates inline, the
    /// post, and the expense that comes out with shares that sum to exactly the total.
    /// </summary>
    [Test]
    public async Task Bill_FromScheduleToPostedExpense_SplitsExactlyAcrossTheHouse()
    {
        var house = await CreateHouseholdAsync("Rent Flat", withFlatmate: true);
        var ledger = house.ChannelId("ledger");

        // Deliberately odd, and deliberately not divisible by two.
        const long totalMinor = 10_001;

        var template = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{ledger}/recurring-expenses", new
            {
                Description = "Rent",
                AmountMinor = totalMinor,
                RecurrenceUnit = "Month",
                RecurrenceInterval = 1,
                AnchorAt = DateTimeOffset.UtcNow,
                SplitKind = "Equal",
                Category = "Rent",
            }),
            "Create the rent schedule");

        Assert.That(template.GetProperty("shares").GetArrayLength(), Is.Zero,
            "an empty Equal split means everyone in the guild, resolved at post time - so somebody "
            + "who moves in next month is included without anybody editing the schedule");

        var bills = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{ledger}/bills"), "List bills"))
            .EnumerateArray().ToList();

        Assert.That(bills, Has.Count.EqualTo(1), "the first period is generated by the create call");

        var bill = bills[0];
        Assert.Multiple(() =>
        {
            Assert.That(bill.GetProperty("status").GetString(), Is.EqualTo("Pending"));
            Assert.That(bill.GetProperty("needsAmount").GetBoolean(), Is.False,
                "a fixed-amount bill never waits for somebody to open the post");
            Assert.That(bill.GetProperty("amountMinor").GetInt64(), Is.EqualTo(totalMinor));
        });

        var posted = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/bills/{bill.GetProperty("id").GetString()}/post", new { }),
            "Post the bill");

        Assert.That(posted.GetProperty("status").GetString(), Is.EqualTo("Posted"));

        var expenseId = posted.GetProperty("expenseId").GetString();
        Assert.That(expenseId, Is.Not.Null.And.Not.Empty,
            "posting returns the bill, not the expense - but it has to say which expense it produced");

        var expenses = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/expenses"), "List expenses");

        var expense = expenses.GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == expenseId);

        var shares = expense.GetProperty("shares").EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(expense.GetProperty("amountMinor").GetInt64(), Is.EqualTo(totalMinor));
            Assert.That(expense.GetProperty("category").GetString(), Is.EqualTo("Rent"),
                "the template's category travels onto the expense it produces");
            Assert.That(shares.Select(s => s.GetProperty("userId").GetString()!),
                Is.EquivalentTo(new[] { _ownerId, _flatmateId }));
            Assert.That(shares.Sum(s => s.GetProperty("amountMinor").GetInt64()), Is.EqualTo(totalMinor),
                "shares always sum to the total - that is what integer minor units are for");
            Assert.That(shares.Select(s => s.GetProperty("amountMinor").GetInt64()),
                Is.EquivalentTo(new long[] { 5001, 5000 }),
                "the odd rappen is distributed deterministically rather than lost");
        });

        // Balances sum to zero and omit anybody square, so a two-person house after one bill is
        // exactly one row: the payer is owed the other person's share.
        var balances = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/ledger/balances"), "Read balances");

        Assert.That(balances.EnumerateArray().Sum(b => b.GetProperty("netMinor").GetInt64()), Is.Zero,
            "balances always sum to zero");

        // A repeat post is answered with the expense the first one produced.
        var repost = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/bills/{bill.GetProperty("id").GetString()}/post", new { }),
            "Post the same bill again");

        Assert.That(repost.GetProperty("expenseId").GetString(), Is.EqualTo(expenseId));

        var afterRepost = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{ledger}/expenses"), "Re-list expenses");

        Assert.That(afterRepost.GetProperty("items").GetArrayLength(), Is.EqualTo(1),
            "posting twice must not produce a second rent expense");
    }

    // ── Chores ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Chore_FromCreationToCompletion_MovesTheFairnessBalance()
    {
        var house = await CreateHouseholdAsync("Rota Flat");
        var chores = house.ChannelId("chores");

        var chore = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{chores}/chores", new
            {
                Title = "Clean the bathroom",
                IntervalDays = 7,
                EffortMinutes = 45,
                RotationRoleId = house.FlatmatesRoleId,
                AnchorAt = DateTimeOffset.UtcNow,
            }),
            "Create the chore");

        var choreId = chore.GetProperty("id").GetString();

        var occurrences = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{chores}/chores/occurrences"), "List occurrences"))
            .EnumerateArray().ToList();

        Assert.That(occurrences, Has.Count.EqualTo(1),
            "the first occurrence is generated on the create call, so a new chore is on the board "
            + "immediately rather than after the first sweep");

        var occurrence = occurrences[0];
        Assert.Multiple(() =>
        {
            Assert.That(occurrence.GetProperty("choreId").GetString(), Is.EqualTo(choreId));
            Assert.That(occurrence.GetProperty("assignedUserId").GetString(), Is.EqualTo(_ownerId),
                "the rotation pool is the Flatmates role's membership, which the seed puts the owner in");
            Assert.That(occurrence.GetProperty("effortMinutes").GetInt32(), Is.EqualTo(45),
                "effort is snapshotted at generation, so re-weighting later never rewrites history");
            Assert.That(occurrence.GetProperty("completedAt").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });

        var occurrenceId = occurrence.GetProperty("id").GetString();

        var balanceBefore = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{chores}/chores/balance"), "Read the balance board");

        Assert.That(
            balanceBefore.EnumerateArray().Single(b => b.GetProperty("userId").GetString() == _ownerId)
                .GetProperty("completedMinutes").GetInt32(),
            Is.Zero, "nothing has been done yet");

        var complete = await _owner.PostAsync($"/api/v1/chore-occurrences/{occurrenceId}/complete", null);
        await E2EAssert.SucceededAsync(complete, _stack.Guild, "Complete the chore failed");

        var afterComplete = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{chores}/chores/occurrences"), "Re-list occurrences"))
            .EnumerateArray().Single(o => o.GetProperty("id").GetString() == occurrenceId);

        Assert.Multiple(() =>
        {
            Assert.That(afterComplete.GetProperty("completedAt").ValueKind, Is.Not.EqualTo(JsonValueKind.Null));
            Assert.That(afterComplete.GetProperty("completedByUserId").GetString(), Is.EqualTo(_ownerId));
            Assert.That(afterComplete.GetProperty("isOverdue").GetBoolean(), Is.False);
        });

        var balanceAfter = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{chores}/chores/balance"), "Re-read the balance board"))
            .EnumerateArray().Single(b => b.GetProperty("userId").GetString() == _ownerId);

        Assert.Multiple(() =>
        {
            Assert.That(balanceAfter.GetProperty("completedMinutes").GetInt32(), Is.EqualTo(45),
                "the balance is weighted by effort, which is what stops the bins counting the same "
                + "as the bathroom");
            Assert.That(balanceAfter.GetProperty("completedCount").GetInt32(), Is.EqualTo(1));
            Assert.That(balanceAfter.GetProperty("presentDays").GetInt32(), Is.GreaterThan(0),
                "presence weighting collapses to the flat average when nobody has declared an absence");
        });

        // Skipping is not doing.
        var skip = await _owner.PostAsync($"/api/v1/chore-occurrences/{occurrenceId}/skip", null);
        await E2EAssert.SucceededAsync(skip, _stack.Guild, "Skip the chore failed");

        var balanceAfterSkip = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{chores}/chores/balance"), "Read the balance after skipping"))
            .EnumerateArray().Single(b => b.GetProperty("userId").GetString() == _ownerId);

        Assert.That(balanceAfterSkip.GetProperty("completedMinutes").GetInt32(), Is.Zero,
            "skipping must take the credit back - a skip is not a completion");
    }

    // ── Pantry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The restock loop, which is the piece that makes the pantry worth keeping up to date: stock
    /// falling to its threshold puts itself on the shopping list, and putting stock back ticks the
    /// line off and re-arms the loop for next time.
    /// </summary>
    [Test]
    public async Task PantryItem_GoingLow_ListsItself_AndRestockingReArmsTheLoop()
    {
        var house = await CreateHouseholdAsync("Pantry Flat");
        var pantry = house.ChannelId("pantry");
        var groceries = house.ChannelId("groceries");

        var config = await ReadJsonAsync(
            await _owner.PutAsJsonAsync($"/api/v1/channels/{pantry}/pantry/config",
                new { RestockListChannelId = groceries }),
            "Point the pantry at the shopping list");

        Assert.That(config.GetProperty("restockListChannelId").GetString(), Is.EqualTo(groceries));

        var milk = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{pantry}/pantry-items",
                new { Name = "Milk", Quantity = 2m, Unit = "l", LowThreshold = 1m }),
            "Stock the milk");

        var milkId = milk.GetProperty("id").GetString();

        Assert.Multiple(() =>
        {
            Assert.That(milk.GetProperty("isLow").GetBoolean(), Is.False);
            Assert.That(milk.GetProperty("restockedAt").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });

        Assert.That(
            (await ReadJsonAsync(await _owner.GetAsync($"/api/v1/channels/{groceries}/list-items"), "Read the list"))
            .GetArrayLength(),
            Is.Zero, "nothing is on the list while the milk is above its threshold");

        // Down to the threshold, which is at-or-below and therefore low.
        var consumed = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/pantry-items/{milkId}/consume", new { Amount = 1m }),
            "Drink a litre");

        Assert.Multiple(() =>
        {
            Assert.That(consumed.GetProperty("quantity").GetDecimal(), Is.EqualTo(1m));
            Assert.That(consumed.GetProperty("isLow").GetBoolean(), Is.True);
            Assert.That(consumed.GetProperty("restockedAt").ValueKind, Is.Not.EqualTo(JsonValueKind.Null),
                "restockedAt is the idempotency guard - it is stamped the moment the line is added");
        });

        var listed = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{groceries}/list-items"), "Re-read the list"))
            .EnumerateArray().ToList();

        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(listed[0].GetProperty("text").GetString(), Is.EqualTo("Milk"));
            Assert.That(listed[0].GetProperty("sourcePantryItemId").GetString(), Is.EqualTo(milkId),
                "the line carries where it came from so a client can badge it 'added by the pantry'");
            Assert.That(listed[0].GetProperty("section").GetString(), Is.EqualTo("Restock"));
        });

        // Still low, and already listed: the guard is what stops the list becoming noise inside a
        // week.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/pantry-items/{milkId}/consume", new { Amount = 0.5m }),
            "Drink some more");

        Assert.That(
            (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{groceries}/list-items"), "Read the list again"))
            .GetArrayLength(),
            Is.EqualTo(1), "a second dip below the threshold must not append a duplicate line");

        // Bought it.
        var restocked = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/pantry-items/{milkId}/restock", new { Amount = 4m }),
            "Put four litres back");

        Assert.Multiple(() =>
        {
            Assert.That(restocked.GetProperty("quantity").GetDecimal(), Is.EqualTo(4.5m));
            Assert.That(restocked.GetProperty("isLow").GetBoolean(), Is.False);
            Assert.That(restocked.GetProperty("restockedAt").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "clearing the stamp is what re-arms the loop");
        });

        var openLines = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{groceries}/list-items"), "Read the open list");
        Assert.That(openLines.GetArrayLength(), Is.Zero, "the pantry ticks its own line off when stock arrives");

        var everyLine = (await ReadJsonAsync(
                await _owner.GetAsync($"/api/v1/channels/{groceries}/list-items?includeChecked=true"),
                "Read the whole list"))
            .EnumerateArray().ToList();

        Assert.That(everyLine, Has.Count.EqualTo(1));
        Assert.That(everyLine[0].GetProperty("isChecked").GetBoolean(), Is.True,
            "bought, not abandoned - deleting the line is the gesture that means 'we are not buying this'");

        // And round again: the loop is armed, so the next dip lists it a second time.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/pantry-items/{milkId}/consume", new { All = true }),
            "Finish the milk");

        var relisted = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/channels/{groceries}/list-items"), "Read the re-armed list");

        Assert.That(relisted.GetArrayLength(), Is.EqualTo(1),
            "buying it re-arms the loop, so going low again asks for it again");
    }

    // ── Feature gating ───────────────────────────────────────────────────────

    /// <summary>
    /// A Community guild has none of the household modules, and the owner is not exempt.
    /// </summary>
    [Test]
    public async Task CommunityGuild_RefusesEveryHouseholdEndpoint_EvenForItsOwner()
    {
        var guild = await ReadJsonAsync(
            await _owner.PostAsJsonAsync("/api/v1/guilds", new { Name = "Just A Server" }), "Create a Community guild");
        var guildId = guild.GetProperty("id").GetString()!;

        Assert.That(guild.GetProperty("ownerId").GetString(), Is.EqualTo(_ownerId),
            "the caller below is the owner, which is the whole point of this test");

        foreach (var path in new[]
                 {
                     $"/api/v1/guilds/{guildId}/home-status",
                     $"/api/v1/guilds/{guildId}/quiet-hours",
                     $"/api/v1/guilds/{guildId}/pantry/expiring",
                     $"/api/v1/guilds/{guildId}/pantry/barcodes",
                     $"/api/v1/guilds/{guildId}/maintenance/attention",
                 })
        {
            var response = await _owner.GetAsync(path);
            await E2EAssert.HasStatusAsync(response, HttpStatusCode.Forbidden, _stack.Guild,
                $"{path} must be forbidden in a Community guild, owner included");
        }

        // The channel containers are refused too, and with a message rather than a 403: the type is
        // not enabled, which is a different sentence from "you may not".
        foreach (var type in new[] { "List", "Chores", "Ledger", "Pantry", "Decisions", "Meals", "Maintenance" })
        {
            var response = await _owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/channels",
                new { Name = $"nope-{type.ToLowerInvariant()}", Description = "", Type = type });

            await E2EAssert.HasStatusAsync(response, HttpStatusCode.BadRequest, _stack.Guild,
                $"a Community guild must refuse a {type} channel");

            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("not enabled"));
        }

        // The digest is the one household surface a Community guild answers, because it is gated on
        // membership rather than on a feature: telling an outsider there is a ledger they cannot see
        // would be a disclosure for no gain, so every section is simply null.
        var digest = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/guilds/{guildId}/home"), "Read the digest of a Community guild");

        Assert.Multiple(() =>
        {
            foreach (var section in new[]
                     {
                         "chores", "lists", "pantry", "ledger", "decisions", "homeStatus",
                         "bills", "meals", "maintenance", "away",
                     })
            {
                // Absent counts as null: whether the serializer emits a null property or omits it
                // is a serializer setting, and either way the client renders nothing.
                var present = digest.TryGetProperty(section, out var value);
                Assert.That(!present || value.ValueKind == JsonValueKind.Null, Is.True,
                    $"'{section}' must be null when the module is off, but was {value}");
            }
        });
    }

    // ── The digest ───────────────────────────────────────────────────────────

    [Test]
    public async Task HomeDigest_ReturnsTheWaveTwoSections_And304sOnAMatchingETag()
    {
        var house = await CreateHouseholdAsync("Digest Flat");

        // One row in each of the three sections wave two added, so a null would be a real failure
        // rather than an empty house.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("ledger")}/recurring-expenses", new
            {
                Description = "Internet",
                AmountMinor = 5900,
                RecurrenceUnit = "Month",
                AnchorAt = DateTimeOffset.UtcNow,
            }),
            "Create the internet schedule");

        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("meals")}/meal-plan", new
            {
                Date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                Slot = "Dinner",
                FreeText = "curry",
                CookUserId = _ownerId,
            }),
            "Plan tonight's dinner");

        var boiler = await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("upkeep")}/maintenance-assets",
                new { Name = "Boiler" }),
            "Catalogue the boiler");

        var brokenResponse = await _owner.PutAsJsonAsync(
            $"/api/v1/maintenance-assets/{boiler.GetProperty("id").GetString()}/status",
            new { Status = "Broken", Note = "no hot water" });
        await E2EAssert.SucceededAsync(brokenResponse, _stack.Guild, "Mark the boiler broken failed");

        var response = await _owner.GetAsync($"/api/v1/guilds/{house.GuildId}/home");
        await E2EAssert.SucceededAsync(response, _stack.Guild, "Read the digest failed");

        var digest = await response.Content.ReadFromJsonAsync<JsonElement>();

        var bills = digest.GetProperty("bills");
        var dueSoon = bills.GetProperty("dueSoon").EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(dueSoon, Has.Count.EqualTo(1));
            Assert.That(dueSoon[0].GetProperty("description").GetString(), Is.EqualTo("Internet"));
            Assert.That(dueSoon[0].GetProperty("status").GetString(), Is.EqualTo("Pending"));
            Assert.That(dueSoon[0].GetProperty("myShareMinor").GetInt64(), Is.EqualTo(5900),
                "the caller is the only member, so the whole bill is their share");
            Assert.That(bills.GetProperty("needsAmountCount").GetInt32(), Is.Zero,
                "a fixed-amount bill is never waiting for somebody to open the post");
        });

        var meals = digest.GetProperty("meals");
        Assert.Multiple(() =>
        {
            Assert.That(meals.GetProperty("today").GetArrayLength(), Is.EqualTo(1));
            Assert.That(meals.GetProperty("today")[0].GetProperty("slot").GetString(), Is.EqualTo("Dinner"));
            Assert.That(meals.GetProperty("imCookingToday").GetBoolean(), Is.True);
        });

        var maintenance = digest.GetProperty("maintenance");
        Assert.Multiple(() =>
        {
            Assert.That(maintenance.GetProperty("brokenCount").GetInt32(), Is.EqualTo(1));
            Assert.That(maintenance.GetProperty("attention").GetArrayLength(), Is.EqualTo(1));
            Assert.That(maintenance.GetProperty("attention")[0].GetProperty("reason").GetString(),
                Is.EqualTo("broken"),
                "the widget line carries one token; the attention board carries all of them");
        });

        Assert.That(digest.GetProperty("away").ValueKind, Is.EqualTo(JsonValueKind.Array),
            "away is a list, empty rather than null, while the Presence module is on");

        // ── Conditional requests ─────────────────────────────────────────────

        var etag = response.Headers.ETag;
        Assert.That(etag, Is.Not.Null, "the digest carries a strong ETag over its own content");
        Assert.That(etag!.IsWeak, Is.False);

        Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("private").And.Contain("no-cache"),
            "the digest is per-user and must never land in a shared cache");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guilds/{house.GuildId}/home");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag.Tag);

        var notModified = await _owner.SendAsync(conditional);
        await E2EAssert.HasStatusAsync(notModified, HttpStatusCode.NotModified, _stack.Guild,
            "an unchanged digest must answer 304");

        // Some HTTP stacks weaken an ETag when they revalidate, and rejecting the value we issued
        // ourselves would silently turn every conditional request back into a full response.
        using var weakened = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guilds/{house.GuildId}/home");
        weakened.Headers.TryAddWithoutValidation("If-None-Match", $"W/{etag.Tag}");

        var weakNotModified = await _owner.SendAsync(weakened);
        await E2EAssert.HasStatusAsync(weakNotModified, HttpStatusCode.NotModified, _stack.Guild,
            "a W/-prefixed ETag we issued ourselves must still match");

        // And a real change invalidates it, which is the half that proves the 304 above meant
        // something.
        await ReadJsonAsync(
            await _owner.PostAsJsonAsync($"/api/v1/channels/{house.ChannelId("groceries")}/list-items",
                new { Text = "onions" }),
            "Add something to the shopping list");

        using var stale = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guilds/{house.GuildId}/home");
        stale.Headers.TryAddWithoutValidation("If-None-Match", etag.Tag);

        var changed = await _owner.SendAsync(stale);
        await E2EAssert.SucceededAsync(changed, _stack.Guild, "A changed digest must not answer 304");
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>One household guild, with its seeded structure resolved so a test can name a
    /// channel instead of carrying an id around.</summary>
    private sealed record Household(
        string GuildId,
        JsonElement Raw,
        IReadOnlyDictionary<string, string> ChannelIds,
        IReadOnlyDictionary<string, string> ChannelTypes,
        IReadOnlyList<string> CategoryNames,
        string FlatmatesRoleId)
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

        // Re-read rather than trusting the create response: the seeded tree is written in the same
        // unit of work but the response is built from the aggregate before its navigations are
        // loaded back, and this is also the call a real client makes.
        var guild = await ReadJsonAsync(
            await _owner.GetAsync($"/api/v1/guilds/{guildId}"), "Read the household back");

        var channels = guild.GetProperty("channels").EnumerateArray().ToList();

        return new Household(
            guildId,
            guild,
            channels.ToDictionary(
                c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetString()!),
            channels.ToDictionary(
                c => c.GetProperty("name").GetString()!, c => c.GetProperty("type").GetString()!),
            guild.GetProperty("categories").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!).ToList(),
            guild.GetProperty("roles").EnumerateArray()
                .Single(r => r.GetProperty("name").GetString() == "Flatmates")
                .GetProperty("id").GetString()!);
    }

    private async Task AssertFullyMigratedAsync<TEnum>(string postgresTypeName) where TEnum : struct, Enum
    {
        var labels = await GuildDatabase.EnumLabelsAsync(_stack, postgresTypeName);

        var missing = Enum.GetNames<TEnum>()
            .Select(GuildDatabase.ToPostgresLabel)
            .Where(label => !labels.Contains(label))
            .ToList();

        Assert.That(missing, Is.Empty,
            $"{typeof(TEnum).Name} has member(s) with no label on the Postgres type '{postgresTypeName}'. "
            + "Npgsql maps this enum by name, so writing one of these throws at runtime and the "
            + "service may not start at all. Generate a migration for it - and note that adding the "
            + "member to a Designer snapshot by hand is what leaves the two out of step in the first "
            + $"place.\nPostgres has: {string.Join(", ", labels.Order(StringComparer.Ordinal))}");
    }

    private static HttpClient AuthedClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient { BaseAddress = service.Client.BaseAddress };
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
