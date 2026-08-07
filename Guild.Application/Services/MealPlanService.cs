using Guild.Application.Dtos.Response;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>The two things the meals module is actually for.</summary>
public class MealPlanService(
    MicroserviceContext ctx,
    HouseholdChannelService household,
    GuildPermissionService permissions,
    MealAlertService alerts)
{
    /// <summary>Ceiling on lines one generation appends.</summary>
    public const int MaxGeneratedLines = 100;

    public const int DefaultExpiringDays = 5;
    public const int DefaultCookableLimit = 20;
    public const int MaxCookableLimit = 100;

    // ── Plan to shopping list ────────────────────────────────────────────────

    /// <summary>One ingredient line of one planned recipe, carried through selection.</summary>
    public sealed record PlannedIngredient(string RecipeTitle, string Text, string? MatchName);

    /// <summary>What survived selection and what did not, with the reason kept per line.</summary>
    public sealed record ShoppingListSelection(
        IReadOnlyList<PlannedIngredient> Take,
        IReadOnlyList<string> SkippedInPantry,
        IReadOnlyList<string> SkippedOnList,
        bool Truncated);

    public sealed class ShoppingListPlanRequest
    {
        public required string MealsChannelId { get; init; }
        public required string GuildId { get; init; }
        public required Channel ListChannel { get; init; }
        public string? PantryChannelId { get; init; }
        public required DateOnly From { get; init; }
        public required DateOnly To { get; init; }
        public bool IncludeOptional { get; init; }
        public bool SkipPantry { get; init; }
        public required string ActorUserId { get; init; }
    }

    /// <summary>Which list a generation writes to: what the caller named, else what the channel is
    /// configured with. Null means the caller has to say - inventing a target here would append a
    /// week's groceries to whichever list happened to sort first.</summary>
    public static string? ResolveTargetListChannelId(string? requested, string? configured) =>
        string.IsNullOrWhiteSpace(requested) ? configured : requested;

    /// <summary>Decides what a generation adds, given everything it needs already loaded.</summary>
    public static ShoppingListSelection SelectForShoppingList(
        IReadOnlyList<PlannedIngredient> candidates,
        IReadOnlyCollection<string> pantryNames,
        IReadOnlyCollection<string> listTexts,
        int cap = MaxGeneratedLines)
    {
        var take = new List<PlannedIngredient>();
        var skippedInPantry = new List<string>();
        var skippedOnList = new List<string>();
        var truncated = false;

        foreach (var candidate in candidates)
        {
            if (pantryNames.Any(name => IngredientMatch.Matches(candidate.MatchName, name)))
            {
                skippedInPantry.Add(candidate.Text);
                continue;
            }

            if (listTexts.Any(text => IngredientMatch.Matches(candidate.MatchName, text)))
            {
                skippedOnList.Add(candidate.Text);
                continue;
            }

            if (take.Count >= cap)
            {
                truncated = true;
                continue;
            }

            // Two recipes on the same plan both wanting onions each get their own line, under their
            // own recipe heading.
            take.Add(candidate);
        }

        return new ShoppingListSelection(take, skippedInPantry, skippedOnList, truncated);
    }

    /// <summary>
    /// Collapses the week's plan into shopping list lines, drops what the house already has, and
    /// appends the rest to the target list.
    /// </summary>
    public async Task<ShoppingListResultDto> AppendPlanToListAsync(ShoppingListPlanRequest request)
    {
        var entries = await ctx.Set<MealPlanEntry>().AsNoTracking()
            .Where(e => e.ChannelId == request.MealsChannelId
                        && e.RecipeId != null
                        && e.Date >= request.From
                        && e.Date <= request.To)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Slot)
            .ThenBy(e => e.Position)
            .ToListAsync();

        // Distinct in plan order, so the generated list reads down the week rather than in
        // whatever order the recipe rows come back.
        var recipeIds = entries.Select(e => e.RecipeId!).Distinct(StringComparer.Ordinal).ToList();

        if (recipeIds.Count == 0)
            return Empty();

        var recipes = await ctx.Set<Recipe>().AsNoTracking()
            .Include(r => r.Ingredients)
            .Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id);

        var candidates = new List<PlannedIngredient>();

        foreach (var recipeId in recipeIds)
        {
            if (!recipes.TryGetValue(recipeId, out var recipe)) continue;

            foreach (var ingredient in recipe.Ingredients.OrderBy(i => i.Position))
            {
                if (ingredient.IsOptional && !request.IncludeOptional) continue;
                candidates.Add(new PlannedIngredient(recipe.Title, ingredient.Text, ingredient.MatchName));
            }
        }

        if (candidates.Count == 0) return Empty();

        var pantryNames = await PantryStockNamesAsync(request);

        var listTexts = await ctx.ListItems.AsNoTracking()
            .Where(i => i.ChannelId == request.ListChannel.Id && !i.IsChecked)
            .Select(i => i.Text)
            .ToListAsync();

        var selection = SelectForShoppingList(candidates, pantryNames, listTexts);

        var maxPosition = await ctx.ListItems
            .Where(i => i.ChannelId == request.ListChannel.Id)
            .Select(i => (int?)i.Position)
            .MaxAsync() ?? -1;

        var added = new List<ListItem>(selection.Take.Count);

        foreach (var planned in selection.Take)
        {
            var item = ListItem.Create(new CreateListItemParams
            {
                ChannelId = request.ListChannel.Id,
                GuildId = request.ListChannel.GuildId,
                Text = planned.Text,
                // The recipe title, so the shopper can see what each line is for.
                Section = planned.RecipeTitle,
                AddedByUserId = request.ActorUserId,
                Position = ++maxPosition,
            });

            ctx.ListItems.Add(item);
            added.Add(item);
        }

        if (added.Count > 0) await ctx.SaveChangesAsync();

        // After the commit, never before: an event for a transaction that then failed sends every
        // open client a line that does not exist. Same split as PantryRestockService.
        foreach (var item in added) await BroadcastListItemAsync(item);

        return new ShoppingListResultDto
        {
            Added = added.Select(ToListItemDto).ToList(),
            SkippedInPantry = selection.SkippedInPantry,
            SkippedOnList = selection.SkippedOnList,
            Truncated = selection.Truncated,
        };
    }

    /// <summary>The stock the "do we already have this" step reads, or nothing at all.</summary>
    private async Task<List<string>> PantryStockNamesAsync(ShoppingListPlanRequest request)
    {
        if (request.SkipPantry || request.PantryChannelId is null) return [];
        if (!await permissions.IsFeatureEnabledAsync(request.GuildId, GuildFeatures.Pantry)) return [];

        return await ctx.PantryItems.AsNoTracking()
            .Where(p => p.ChannelId == request.PantryChannelId && p.Quantity > 0)
            .Select(p => p.Name)
            .ToListAsync();
    }

    private static ShoppingListResultDto Empty() => new()
    {
        Added = [],
        SkippedInPantry = [],
        SkippedOnList = [],
        Truncated = false,
    };

    /// <summary>Same event and same payload shape as
    /// <see cref="PantryRestockService.BroadcastRestockAsync"/>, deliberately: a client already
    /// renders a line that appeared on its list without it asking, and a second event name for the
    /// identical thing would mean it silently stops rendering half of them.</summary>
    private async Task BroadcastListItemAsync(ListItem listItem) =>
        await household.BroadcastAsync(listItem.GuildId, listItem.ChannelId, "guild.ListItemCreated", new
        {
            GuildId = listItem.GuildId,
            ChannelId = listItem.ChannelId,
            Item = new
            {
                listItem.Id,
                listItem.ChannelId,
                listItem.Text,
                listItem.Quantity,
                listItem.Section,
                listItem.Position,
                listItem.SourcePantryItemId,
                IsChecked = false,
            },
        });

    private static ListItemDto ToListItemDto(ListItem item) => new()
    {
        Id = item.Id,
        ChannelId = item.ChannelId,
        Text = item.Text,
        Quantity = item.Quantity,
        Note = item.Note,
        Section = item.Section,
        AssigneeUserId = item.AssigneeUserId,
        AddedByUserId = item.AddedByUserId,
        IsChecked = item.IsChecked,
        CheckedAt = item.CheckedAt,
        CheckedByUserId = item.CheckedByUserId,
        Position = item.Position,
        SourcePantryItemId = item.SourcePantryItemId,
        CreatedAt = item.CreatedAt,
    };

    // ── Cookable ─────────────────────────────────────────────────────────────

    public sealed record CookableIngredient(string Text, string? MatchName, bool IsOptional);

    public sealed record CookableCandidate(string RecipeId, string Title, IReadOnlyList<CookableIngredient> Ingredients);

    /// <summary>One thing the pantry has, and whether it is inside the expiry horizon.</summary>
    public sealed record CookableStock(string Name, bool IsExpiring);

    public sealed record CookableRanking(
        string RecipeId,
        int HaveCount,
        int MissingCount,
        int ExpiringCount,
        IReadOnlyList<string> ExpiringNames,
        IReadOnlyList<string> Missing);

    /// <summary>Ranks recipes by how much about-to-expire stock they use up.</summary>
    public static List<CookableRanking> RankCookable(
        IReadOnlyList<CookableCandidate> candidates,
        IReadOnlyList<CookableStock> stock,
        int limit = DefaultCookableLimit)
    {
        var ranked = new List<(CookableCandidate Candidate, CookableRanking Ranking)>();

        foreach (var candidate in candidates)
        {
            var have = 0;
            var missing = new List<string>();
            var expiringNames = new List<string>();

            foreach (var ingredient in candidate.Ingredients)
            {
                var covering = stock
                    .Where(s => IngredientMatch.Matches(ingredient.MatchName, s.Name))
                    .ToList();

                if (covering.Count == 0)
                {
                    // Optional lines never count as missing.
                    if (!ingredient.IsOptional) missing.Add(ingredient.Text);
                    continue;
                }

                have++;

                foreach (var expiring in covering.Where(s => s.IsExpiring))
                {
                    if (!expiringNames.Contains(expiring.Name, StringComparer.OrdinalIgnoreCase))
                        expiringNames.Add(expiring.Name);
                }
            }

            ranked.Add((candidate, new CookableRanking(
                candidate.RecipeId, have, missing.Count, expiringNames.Count, expiringNames, missing)));
        }

        return ranked
            .OrderByDescending(r => r.Ranking.ExpiringCount)
            .ThenBy(r => r.Ranking.MissingCount)
            .ThenBy(r => r.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, MaxCookableLimit))
            .Select(r => r.Ranking)
            .ToList();
    }

    /// <summary>The food-waste board for one meals channel.</summary>
    public async Task<CookableResultDto> RankCookableAsync(
        string channelId, string guildId, int expiringDays, int limit)
    {
        var config = await ctx.Set<MealPlanConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChannelId == channelId);

        if (config?.PantryChannelId is null)
            return new CookableResultDto { Items = [], Reason = "No pantry is linked to this meal plan." };

        if (!await permissions.IsFeatureEnabledAsync(guildId, GuildFeatures.Pantry))
            return new CookableResultDto { Items = [], Reason = "The pantry module is off for this house." };

        var horizon = DateTimeOffset.UtcNow.AddDays(expiringDays);

        var stock = await ctx.PantryItems.AsNoTracking()
            .Where(p => p.ChannelId == config.PantryChannelId && p.Quantity > 0)
            .Select(p => new { p.Name, p.ExpiresAt })
            .ToListAsync();

        if (stock.Count == 0)
            return new CookableResultDto { Items = [], Reason = "The linked pantry has nothing in stock." };

        var recipes = await ctx.Set<Recipe>().AsNoTracking()
            .Include(r => r.Ingredients)
            .Where(r => r.ChannelId == channelId)
            .ToListAsync();

        if (recipes.Count == 0)
            return new CookableResultDto { Items = [], Reason = "This channel has no recipes yet." };

        var candidates = recipes
            .Select(r => new CookableCandidate(r.Id, r.Title, r.Ingredients
                .OrderBy(i => i.Position)
                .Select(i => new CookableIngredient(i.Text, i.MatchName, i.IsOptional))
                .ToList()))
            .ToList();

        var stockNames = stock
            .Select(s => new CookableStock(s.Name, s.ExpiresAt is not null && s.ExpiresAt <= horizon))
            .ToList();

        var rankings = RankCookable(candidates, stockNames, limit);
        var byId = recipes.ToDictionary(r => r.Id);

        return new CookableResultDto
        {
            Items = rankings.Select(r => new CookableRecipeDto
            {
                Recipe = ToRecipeDto(byId[r.RecipeId]),
                HaveCount = r.HaveCount,
                MissingCount = r.MissingCount,
                ExpiringCount = r.ExpiringCount,
                ExpiringNames = r.ExpiringNames,
                Missing = r.Missing,
            }).ToList(),
        };
    }

    public static RecipeDto ToRecipeDto(Recipe recipe) => new()
    {
        Id = recipe.Id,
        ChannelId = recipe.ChannelId,
        Title = recipe.Title,
        Description = recipe.Description,
        Servings = recipe.Servings,
        PrepMinutes = recipe.PrepMinutes,
        SourceUrl = recipe.SourceUrl,
        CreatedByUserId = recipe.CreatedByUserId,
        Ingredients = recipe.Ingredients
            .OrderBy(i => i.Position)
            .Select(i => new RecipeIngredientDto
            {
                Position = i.Position,
                Text = i.Text,
                MatchName = i.MatchName,
                IsOptional = i.IsOptional,
            })
            .ToList(),
        CreatedAt = recipe.CreatedAt,
    };

    // ── Sweep ────────────────────────────────────────────────────────────────

    /// <summary>Periodic work for the meals module.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default) =>
        await alerts.SendCookingTodayAsync(ct);
}
