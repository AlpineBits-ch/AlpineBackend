using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>The three low-friction ways to change stock: scan, consume, restock.</summary>
public class PantryCaptureService(
    MicroserviceContext ctx, PantryRestockService restock, HouseholdChannelService household,
    ProductCatalogService catalog)
{
    /// <summary>Long enough for every retail symbology plus the QR codes people scan by mistake,
    /// short enough that the column is not a free-text field.</summary>
    public const int MaxBarcodeLength = 64;

    private const int MaxNameLength = 100;

    /// <summary>What a scan adds when neither the request nor the learned barcode says.</summary>
    private const decimal DefaultScanQuantity = 1m;

    /// <summary>Everything one capture staged, so the endpoint can commit once and then publish
    /// once.</summary>
    public class CaptureResult
    {
        public required PantryItem Item { get; init; }

        /// <summary>The item is new, so this broadcasts as a create rather than an update.</summary>
        public bool Created { get; init; }

        /// <summary>This guild had never seen the barcode before and now has - see
        /// <see cref="ScanPantryItemResultDto.Learned"/>.</summary>
        public bool Learned { get; init; }

        /// <summary>
        /// Set when the shared product catalog answered the code, null otherwise.
        /// </summary>
        public ProductCatalogService.Match? Catalog { get; init; }

        public PantryRestockService.StockChange Change { get; init; }

        public IReadOnlyList<ListItem> CheckedLines { get; init; } = [];
    }

    public static PantryItemDto ToDto(PantryItem item) => new()
    {
        Id = item.Id,
        ChannelId = item.ChannelId,
        Name = item.Name,
        Quantity = item.Quantity,
        Unit = item.Unit,
        LowThreshold = item.LowThreshold,
        ExpiresAt = item.ExpiresAt,
        IsLow = item.IsLow(),
        RestockedAt = item.RestockedAt,
        Barcode = item.Barcode,
        AddedByUserId = item.AddedByUserId,
    };

    /// <summary>
    /// Stocks whatever was scanned, in steps that get progressively less certain about what the
    /// code means.
    /// </summary>
    public async Task<(CaptureResult? Result, string? Error)> ScanAsync(
        string channelId, string guildId, ScanPantryItemDto dto, string userId,
        IReadOnlyList<string>? languages = null)
    {
        if (string.IsNullOrWhiteSpace(dto.Barcode)) return (null, "Barcode is required");

        var code = dto.Barcode.Trim();
        if (code.Length > MaxBarcodeLength)
            return (null, $"Barcode must be {MaxBarcodeLength} characters or fewer");

        if (dto.Quantity is <= 0) return (null, "Quantity must be greater than zero");

        var name = string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim();
        if (name is not null && name.Length > MaxNameLength)
            return (null, $"Name must be {MaxNameLength} characters or fewer");

        var known = await ctx.Set<PantryBarcode>()
            .FirstOrDefaultAsync(b => b.GuildId == guildId && b.Barcode == code);

        // Oldest first, so a channel that somehow holds two rows for one code tops up the original
        // rather than forking a third.
        var existing = await ctx.PantryItems
            .Where(i => i.ChannelId == channelId && i.Barcode == code)
            .OrderBy(i => i.CreatedAt)
            .FirstOrDefaultAsync();

        // Only when nothing nearer already has a name.
        var match = name is null && known is null
            ? await catalog.ResolveForScanAsync(code, languages)
            : null;

        var amount = dto.Quantity ?? known?.DefaultQuantity ?? DefaultScanQuantity;
        var now = DateTimeOffset.UtcNow;

        PantryItem item;
        bool created;

        if (existing is not null)
        {
            item = existing;
            created = false;
            item.Quantity += amount;

            // Only when the scan actually said so.
            if (name is not null) item.Name = name;
            if (dto.Unit is not null) item.Unit = dto.Unit;

            item.UpdatedAt = now;
        }
        else
        {
            // The catalog sits last, after both of the house's own answers, and it fills only the
            // name.
            var itemName = name ?? known?.Name ?? match?.Name;
            if (string.IsNullOrWhiteSpace(itemName))
                return (null, "Name is required the first time a barcode is scanned here");

            item = PantryItem.Create(new CreatePantryItemParams
            {
                ChannelId = channelId,
                GuildId = guildId,
                Name = itemName,
                Quantity = amount,
                Unit = dto.Unit ?? known?.Unit,
                LowThreshold = known?.LowThreshold,
                ExpiresAt = dto.ExpiresAt,
                Barcode = code,
                AddedByUserId = userId,
            });

            ctx.PantryItems.Add(item);
            created = true;
        }

        // Moving the date makes any previous warning stale, so the stamp is released and the sweep
        // may warn again - the same release PATCH does, and for the same reason: a replacement
        // carton would otherwise inherit the old one's spent warning.
        if (dto.ExpiresAt is not null && dto.ExpiresAt != item.ExpiresAt)
        {
            item.ExpiresAt = dto.ExpiresAt;
            item.ExpiryNotifiedAt = null;
        }

        var change = await restock.ApplyStockChangeAsync(item);

        // A new row cannot have a list line pointing at it yet, so this is skipped rather than run
        // for an empty result.
        var checkedLines = created
            ? []
            : await restock.StageSourceLineCheckAsync(item, userId);

        // Not learned when the catalog is what supplied the name, and this is a licence boundary
        // rather than a nicety.
        var learned = match is null && Learn(known, guildId, code, item, dto.Quantity);

        return (new CaptureResult
        {
            Item = item,
            Created = created,
            Learned = learned,
            Catalog = match,
            Change = change,
            CheckedLines = checkedLines,
        }, null);
    }

    /// <summary>Everything one <see cref="TeachBarcodeAsync"/> staged.</summary>
    public class TeachResult
    {
        public required PantryBarcode Barcode { get; init; }

        /// <summary>The guild had no row for this code before.</summary>
        public required bool Learned { get; init; }

        public IReadOnlyList<PantryItem> RenamedItems { get; init; } = [];
    }

    /// <summary>The house states what a barcode is called.</summary>
    public async Task<(TeachResult? Result, string? Error)> TeachBarcodeAsync(
        string guildId, string barcode, TeachPantryBarcodeDto dto)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return (null, "Barcode is required");

        var code = barcode.Trim();
        if (code.Length > MaxBarcodeLength)
            return (null, $"Barcode must be {MaxBarcodeLength} characters or fewer");

        if (string.IsNullOrWhiteSpace(dto.Name)) return (null, "Name is required");

        var name = dto.Name.Trim();
        if (name.Length > MaxNameLength)
            return (null, $"Name must be {MaxNameLength} characters or fewer");

        // Rejected rather than ignored: a client sending zero means something by it, and the only
        // thing it could mean here is a scan that adds nothing.
        if (dto.DefaultQuantity is <= 0)
            return (null, "DefaultQuantity must be greater than zero");

        var unit = string.IsNullOrWhiteSpace(dto.Unit) ? null : dto.Unit.Trim();

        var known = await ctx.Set<PantryBarcode>()
            .FirstOrDefaultAsync(b => b.GuildId == guildId && b.Barcode == code);

        var learned = known is null;

        if (known is null)
        {
            known = PantryBarcode.Create(new CreatePantryBarcodeParams
            {
                GuildId = guildId,
                Barcode = code,
                Name = name,
                Unit = unit,
                DefaultQuantity = dto.DefaultQuantity is > 0 ? dto.DefaultQuantity.Value : DefaultScanQuantity,
            });

            ctx.Set<PantryBarcode>().Add(known);
        }
        else
        {
            known.StateName(name, unit, dto.DefaultQuantity);
        }

        return (new TeachResult
        {
            Barcode = known,
            Learned = learned,
            RenamedItems = await AdoptNameAsync(guildId, code, name),
        }, null);
    }

    /// <summary>
    /// Renames the items in this guild that are still showing the catalog's suggestion for this
    /// code, and returns them.
    /// </summary>
    private async Task<IReadOnlyList<PantryItem>> AdoptNameAsync(
        string guildId, string code, string name)
    {
        var suggestion = await ctx.ProductCatalogEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Barcode == code);

        if (suggestion is null) return [];

        // Every language the scan could have picked, because a French-speaking flat and a German one
        // scanning the same code get different suggestions from the same row.
        var suggested = new[] { suggestion.NameDe, suggestion.NameFr, suggestion.NameIt, suggestion.NameEn }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (suggested.Count == 0) return [];

        var carrying = await ctx.PantryItems
            .Where(i => i.GuildId == guildId && i.Barcode == code)
            .ToListAsync();

        var renamed = new List<PantryItem>();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in carrying)
        {
            if (item.Name == name || !suggested.Contains(item.Name.Trim())) continue;

            item.Name = name;
            item.UpdatedAt = now;
            renamed.Add(item);
        }

        return renamed;
    }

    /// <summary>Tells the rest of the house about the jars a correction renamed.</summary>
    public async Task PublishTeachAsync(TeachResult result)
    {
        foreach (var item in result.RenamedItems)
            await household.BroadcastAsync(item.GuildId, item.ChannelId, "guild.PantryItemUpdated",
                new { GuildId = item.GuildId, ChannelId = item.ChannelId, Item = ToDto(item) });
    }

    /// <summary>Takes stock off an item and runs the low-stock loop over the result.</summary>
    public async Task<CaptureResult> ConsumeAsync(PantryItem item, decimal amount, bool all)
    {
        item.Quantity = all ? 0m : Math.Max(0m, item.Quantity - amount);
        item.UpdatedAt = DateTimeOffset.UtcNow;

        return new CaptureResult { Item = item, Change = await restock.ApplyStockChangeAsync(item) };
    }

    /// <summary>Puts stock back, releases the stamps if it cleared the threshold, and ticks off
    /// whatever the pantry had put on a shopping list for it.</summary>
    public async Task<CaptureResult> RestockAsync(PantryItem item, decimal amount, string userId)
    {
        item.Quantity += amount;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        var change = await restock.ApplyStockChangeAsync(item);
        var checkedLines = await restock.StageSourceLineCheckAsync(item, userId);

        return new CaptureResult { Item = item, Change = change, CheckedLines = checkedLines };
    }

    /// <summary>
    /// Everything the caller owes the rest of the house once its commit has succeeded.
    /// </summary>
    public async Task PublishAsync(CaptureResult result, string actorUserId)
    {
        var item = result.Item;

        await household.BroadcastAsync(item.GuildId, item.ChannelId,
            result.Created ? "guild.PantryItemCreated" : "guild.PantryItemUpdated",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, Item = ToDto(item) });

        await restock.PublishStockChangeAsync(item, result.Change, actorUserId);

        foreach (var line in result.CheckedLines) await restock.BroadcastCheckedAsync(line);
    }

    /// <summary>Teaches the guild what this code means, or re-teaches it.</summary>
    private bool Learn(
        PantryBarcode? known, string guildId, string code, PantryItem item, decimal? statedQuantity)
    {
        if (known is null)
        {
            known = PantryBarcode.Create(new CreatePantryBarcodeParams
            {
                GuildId = guildId,
                Barcode = code,
                Name = item.Name,
                Unit = item.Unit,
                DefaultQuantity = statedQuantity is > 0 ? statedQuantity.Value : DefaultScanQuantity,
                LowThreshold = item.LowThreshold,
            });

            ctx.Set<PantryBarcode>().Add(known);
            known.Observe(item.Name, item.Unit, item.LowThreshold, statedQuantity);
            return true;
        }

        known.Observe(item.Name, item.Unit, item.LowThreshold, statedQuantity);
        return false;
    }
}
