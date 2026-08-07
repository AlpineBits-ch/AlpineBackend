using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;

namespace Guild.Tests.Services;

/// <summary>
/// The decisions the meals module makes, tested where they are made rather than through a database.
/// </summary>
[TestFixture]
public class MealPlanServiceTests
{
    private static MealPlanService.PlannedIngredient Ingredient(string recipe, string text) =>
        new(recipe, text, IngredientMatch.Normalize(text));

    // ══════════════════════════════════════════════════════════════════════════
    // Plan to shopping list - the endpoint the module exists for
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Select_WithAnEmptyPantryAndAnEmptyList_TakesEverything()
    {
        var candidates = new[]
        {
            Ingredient("Chili", "2 onions"),
            Ingredient("Chili", "500g mince"),
        };

        var selection = MealPlanService.SelectForShoppingList(candidates, [], []);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take.Select(t => t.Text), Is.EqualTo(new[] { "2 onions", "500g mince" }));
            Assert.That(selection.SkippedInPantry, Is.Empty);
            Assert.That(selection.SkippedOnList, Is.Empty);
            Assert.That(selection.Truncated, Is.False);
        });
    }

    [Test]
    public void Select_DropsWhatThePantryAlreadyHas()
    {
        var candidates = new[]
        {
            Ingredient("Chili", "2 onions"),
            Ingredient("Chili", "500g mince"),
        };

        var selection = MealPlanService.SelectForShoppingList(candidates, ["Onions"], []);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take.Select(t => t.Text), Is.EqualTo(new[] { "500g mince" }));
            Assert.That(selection.SkippedInPantry, Is.EqualTo(new[] { "2 onions" }));
        });
    }

    [Test]
    public void Select_DropsWhatIsAlreadyOnTheList()
    {
        var candidates = new[] { Ingredient("Chili", "2 onions") };

        var selection = MealPlanService.SelectForShoppingList(candidates, [], ["onion"]);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take, Is.Empty);
            Assert.That(selection.SkippedOnList, Is.EqualTo(new[] { "2 onions" }));
        });
    }

    /// <summary>A shopper who opens the list and finds no onions on it cannot tell a working pantry
    /// check from a broken button, and will not press it twice. Both reasons are reported, and
    /// separately, because they say different things about the house.</summary>
    [Test]
    public void Select_ReportsBothSkipReasons()
    {
        var candidates = new[]
        {
            Ingredient("Chili", "2 onions"),
            Ingredient("Chili", "rice"),
            Ingredient("Chili", "500g mince"),
        };

        var selection = MealPlanService.SelectForShoppingList(candidates, ["Onions"], ["Rice"]);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take.Select(t => t.Text), Is.EqualTo(new[] { "500g mince" }));
            Assert.That(selection.SkippedInPantry, Is.EqualTo(new[] { "2 onions" }));
            Assert.That(selection.SkippedOnList, Is.EqualTo(new[] { "rice" }));
        });
    }

    /// <summary>Two reasons for one absence reads like a bug.</summary>
    [Test]
    public void Select_ALineInBothIsReportedOnceAsPantry()
    {
        var candidates = new[] { Ingredient("Chili", "2 onions") };

        var selection = MealPlanService.SelectForShoppingList(candidates, ["Onions"], ["onions"]);

        Assert.Multiple(() =>
        {
            Assert.That(selection.SkippedInPantry, Is.EqualTo(new[] { "2 onions" }));
            Assert.That(selection.SkippedOnList, Is.Empty);
        });
    }

    /// <summary>Nothing is scaled or summed, so two recipes wanting onions get two lines under two
    /// headings. Collapsing them would buy one meal's worth for two meals, and the shopper would
    /// have no way to see that had happened.</summary>
    [Test]
    public void Select_TwoRecipesWantingTheSameThingBothGetALine()
    {
        var candidates = new[]
        {
            Ingredient("Chili", "2 onions"),
            Ingredient("Soup", "1 onion"),
        };

        var selection = MealPlanService.SelectForShoppingList(candidates, [], []);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take, Has.Count.EqualTo(2));
            Assert.That(selection.Take.Select(t => t.RecipeTitle), Is.EqualTo(new[] { "Chili", "Soup" }));
            Assert.That(selection.SkippedOnList, Is.Empty);
        });
    }

    [Test]
    public void Select_AnIngredientWithNoMatchNameIsAlwaysBought()
    {
        var candidates = new[] { new MealPlanService.PlannedIngredient("Chili", "???", null) };

        var selection = MealPlanService.SelectForShoppingList(candidates, ["???"], ["???"]);

        Assert.That(selection.Take, Has.Count.EqualTo(1),
            "an unreadable line is added rather than silently matched against everything");
    }

    [Test]
    public void Select_StopsAtTheLineCapAndSaysSo()
    {
        var candidates = Enumerable.Range(0, MealPlanService.MaxGeneratedLines + 20)
            .Select(i => Ingredient("Feast", $"item{i}"))
            .ToList();

        var selection = MealPlanService.SelectForShoppingList(candidates, [], []);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take, Has.Count.EqualTo(MealPlanService.MaxGeneratedLines));
            Assert.That(selection.Truncated, Is.True,
                "silently shipping half a week is how the button stops being trusted");
        });
    }

    [Test]
    public void Select_UnderTheCapIsNotTruncated()
    {
        var candidates = Enumerable.Range(0, MealPlanService.MaxGeneratedLines)
            .Select(i => Ingredient("Feast", $"item{i}"))
            .ToList();

        var selection = MealPlanService.SelectForShoppingList(candidates, [], []);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Take, Has.Count.EqualTo(MealPlanService.MaxGeneratedLines));
            Assert.That(selection.Truncated, Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Which list a generation writes to
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Null here is what the endpoint turns into a 400. Inventing a target would append a
    /// week of groceries to whichever list happened to sort first.</summary>
    [Test]
    public void ResolveTargetList_PrefersTheRequestThenTheConfigThenNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MealPlanService.ResolveTargetListChannelId("chan-asked", "chan-config"),
                Is.EqualTo("chan-asked"));
            Assert.That(MealPlanService.ResolveTargetListChannelId(null, "chan-config"),
                Is.EqualTo("chan-config"));
            Assert.That(MealPlanService.ResolveTargetListChannelId("   ", "chan-config"),
                Is.EqualTo("chan-config"), "blank is not a channel id");
            Assert.That(MealPlanService.ResolveTargetListChannelId(null, null), Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Cookable - the
    // food-waste board ══════════════════════════════════════════════════════════════════════════

    private static MealPlanService.CookableCandidate Candidate(string id, string title, params string[] ingredients) =>
        new(id, title, ingredients
            .Select(text => new MealPlanService.CookableIngredient(
                text, IngredientMatch.Normalize(text), false))
            .ToList());

    /// <summary>The ordering is the feature.</summary>
    [Test]
    public void RankCookable_PutsTheRecipeUsingTheMostExpiringStockFirst()
    {
        var candidates = new[]
        {
            Candidate("r-soup", "Soup", "onion", "stock"),
            Candidate("r-omelette", "Omelette", "eggs", "spinach", "cheese"),
        };

        var stock = new[]
        {
            new MealPlanService.CookableStock("Onions", false),
            new MealPlanService.CookableStock("Stock cubes", false),
            new MealPlanService.CookableStock("Eggs", true),
            new MealPlanService.CookableStock("Spinach", true),
        };

        var ranked = MealPlanService.RankCookable(candidates, stock);

        Assert.Multiple(() =>
        {
            Assert.That(ranked[0].RecipeId, Is.EqualTo("r-omelette"),
                "two things about to go off beats a recipe with nothing missing");
            Assert.That(ranked[0].ExpiringCount, Is.EqualTo(2));
            Assert.That(ranked[0].ExpiringNames, Is.EqualTo(new[] { "Eggs", "Spinach" }));
            Assert.That(ranked[0].Missing, Is.EqualTo(new[] { "cheese" }));
            Assert.That(ranked[1].RecipeId, Is.EqualTo("r-soup"));
        });
    }

    [Test]
    public void RankCookable_TiedOnExpiringStock_PrefersTheOneWithLessToBuy()
    {
        var candidates = new[]
        {
            Candidate("r-long", "Long", "spinach", "cheese", "cream", "nutmeg"),
            Candidate("r-short", "Short", "spinach", "cheese"),
        };

        var stock = new[] { new MealPlanService.CookableStock("Spinach", true) };

        var ranked = MealPlanService.RankCookable(candidates, stock);

        Assert.Multiple(() =>
        {
            Assert.That(ranked.Select(r => r.RecipeId), Is.EqualTo(new[] { "r-short", "r-long" }));
            Assert.That(ranked[0].MissingCount, Is.EqualTo(1));
            Assert.That(ranked[0].HaveCount, Is.EqualTo(1));
        });
    }

    /// <summary>Optional lines are garnish.</summary>
    [Test]
    public void RankCookable_OptionalIngredientsAreNeverMissing()
    {
        var candidates = new[]
        {
            new MealPlanService.CookableCandidate("r-pasta", "Pasta",
            [
                new MealPlanService.CookableIngredient("pasta", "pasta", false),
                new MealPlanService.CookableIngredient("parsley", "parsley", true),
            ]),
        };

        var ranked = MealPlanService.RankCookable(
            candidates, [new MealPlanService.CookableStock("Pasta", true)]);

        Assert.Multiple(() =>
        {
            Assert.That(ranked[0].MissingCount, Is.Zero);
            Assert.That(ranked[0].Missing, Is.Empty);
            Assert.That(ranked[0].HaveCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void RankCookable_WithNothingMatching_StillAnswersInAPredictableOrder()
    {
        var candidates = new[]
        {
            Candidate("r-b", "Bolognese", "mince"),
            Candidate("r-a", "Apple pie", "apples"),
        };

        var ranked = MealPlanService.RankCookable(
            candidates, [new MealPlanService.CookableStock("Bleach", false)]);

        Assert.Multiple(() =>
        {
            Assert.That(ranked.Select(r => r.RecipeId), Is.EqualTo(new[] { "r-a", "r-b" }),
                "tied on both keys, so title order - the same pantry always produces the same list");
            Assert.That(ranked.All(r => r.HaveCount == 0), Is.True);
        });
    }

    [Test]
    public void RankCookable_HonoursTheLimit()
    {
        var candidates = Enumerable.Range(0, 10)
            .Select(i => Candidate($"r-{i}", $"Recipe {i}", "flour"))
            .ToList();

        var ranked = MealPlanService.RankCookable(
            candidates, [new MealPlanService.CookableStock("Flour", true)], limit: 3);

        Assert.That(ranked, Has.Count.EqualTo(3));
    }

    // ══════════════════════════════════════════════════════════════════════════ Cooking-today
    // policy ══════════════════════════════════════════════════════════════════════════

    private static MealPlanEntry Entry(DateOnly date, string? cook = "anna") =>
        MealPlanEntry.Create(new CreateMealPlanEntryParams
        {
            ChannelId = "chan-meals", GuildId = "guild-1", Date = date, Slot = MealSlot.Dinner,
            FreeText = "Curry", CookUserId = cook, CreatedByUserId = "ben",
        });

    [Test]
    public void ShouldNotify_TheCookOnTheDay()
    {
        var today = new DateOnly(2026, 8, 7);

        Assert.That(MealAlertService.ShouldNotify(Entry(today), today), Is.True);
    }

    /// <summary>The at-most-once stamp.</summary>
    [Test]
    public void ShouldNotify_OnlyOnce()
    {
        var today = new DateOnly(2026, 8, 7);
        var entry = Entry(today);

        Assert.That(MealAlertService.ShouldNotify(entry, today), Is.True);

        entry.NotifiedAt = DateTimeOffset.UtcNow;

        Assert.That(MealAlertService.ShouldNotify(entry, today), Is.False);
    }

    /// <summary>"You're down to cook this today" about yesterday is not a late reminder, it is a
    /// wrong one. The sweep retires those silently instead.</summary>
    [Test]
    public void ShouldNotify_NeverForADateAlreadyPast()
    {
        var today = new DateOnly(2026, 8, 7);

        Assert.Multiple(() =>
        {
            Assert.That(MealAlertService.ShouldNotify(Entry(today.AddDays(-1)), today), Is.False);
            Assert.That(MealAlertService.ShouldNotify(Entry(today.AddDays(-30)), today), Is.False);
        });
    }

    [Test]
    public void ShouldNotify_NotAheadOfTimeAndNotWithoutACook()
    {
        var today = new DateOnly(2026, 8, 7);

        Assert.Multiple(() =>
        {
            Assert.That(MealAlertService.ShouldNotify(Entry(today.AddDays(1)), today), Is.False,
                "tomorrow's dinner is not today's problem");
            Assert.That(MealAlertService.ShouldNotify(Entry(today, cook: null), today), Is.False,
                "nobody is down to cook, so there is nobody to tell");
        });
    }
}
