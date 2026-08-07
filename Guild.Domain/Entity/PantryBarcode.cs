using Persistence;

namespace Guild.Domain.Entity;

public class CreatePantryBarcodeParams
{
    public string GuildId { get; set; } = null!;
    public string Barcode { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Unit { get; set; }
    public decimal DefaultQuantity { get; set; } = 1m;
    public decimal? LowThreshold { get; set; }
}

/// <summary>
/// What one house has learned a barcode means: the name it uses for the product, the unit it counts
/// it in, and how much one scan of it adds.
/// </summary>
public class PantryBarcode : BaseEntity<PantryBarcode>, IPrefixedEntity
{
    public static string Prefix { get; } = "pbar";

    /// <summary>The guild, not the channel: a code scanned into the freezer is the same product in
    /// the cellar, and making the house teach it twice is exactly the friction this removes.</summary>
    public string GuildId { get; set; } = null!;

    public string Barcode { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Unit { get; set; }

    /// <summary>How much one scan adds - 1 for a jar, 6 for a box that only ever comes in sixes.
    /// Re-learned whenever a scan states a quantity explicitly, so the house corrects it by using
    /// it rather than by editing a setting.</summary>
    public decimal DefaultQuantity { get; set; } = 1m;

    /// <summary>Remembered from the item so a replacement jar arrives already tracking its
    /// low-stock threshold. Without this, running a product out and re-adding it silently drops it
    /// out of the restock loop - which reads as the loop being unreliable.</summary>
    public decimal? LowThreshold { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }

    /// <summary>How often this code has been scanned here.</summary>
    public int TimesSeen { get; set; }

    public static PantryBarcode Create(CreatePantryBarcodeParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new PantryBarcode
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = @params.GuildId,
            Barcode = @params.Barcode,
            Name = @params.Name,
            Unit = @params.Unit,
            DefaultQuantity = @params.DefaultQuantity,
            LowThreshold = @params.LowThreshold,
            LastUsedAt = date,
            TimesSeen = 0,
        };
    }

    /// <summary>
    /// Records that this code was just scanned, and re-learns what the house now calls it.
    /// </summary>
    public void Observe(string name, string? unit, decimal? lowThreshold, decimal? statedQuantity)
    {
        Name = name;
        Unit = unit;
        LowThreshold = lowThreshold;

        if (statedQuantity is > 0) DefaultQuantity = statedQuantity.Value;

        TimesSeen++;
        LastUsedAt = DateTimeOffset.UtcNow;
        UpdatedAt = LastUsedAt;
    }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<PantryBarcode>(barcodeBuilder =>
// {
//     barcodeBuilder.HasOne<Domain.Aggregates.Guild>()
//         .WithMany()
//         .HasForeignKey(x => x.GuildId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // The upsert every scan runs, and the constraint that makes "learned once" true.
//     barcodeBuilder.HasIndex(x => new { x.GuildId, x.Barcode }).IsUnique();
//
//     // The completion list: this guild's products, most-used first.
//     barcodeBuilder.HasIndex(x => new { x.GuildId, x.TimesSeen });
// });
//
// And, inside the existing modelBuilder.Entity<PantryItem>(itemBuilder => ...) block:
//
//     // Scan's first question: is the thing I am holding already in this fridge. Filtered
//     // because almost nothing in a hand-entered pantry carries a code.
//     itemBuilder.HasIndex(x => new { x.ChannelId, x.Barcode })
//         .HasFilter("barcode IS NOT NULL");
//
// DbSet: public DbSet<PantryBarcode> PantryBarcodes { get; set; }
// MapEnum: none.
