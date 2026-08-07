using Persistence;

namespace Guild.Domain.Entity;

public class CreatePantryItemParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? LowThreshold { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Barcode { get; set; }
    public string AddedByUserId { get; set; } = null!;
}

/// <summary>Stock of one thing in one location.</summary>
public class PantryItem : BaseEntity<PantryItem>, IPrefixedEntity
{
    public static string Prefix { get; } = "pitm";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Decimal here, unlike ListItem.Quantity's free text, because this one is actually
    /// compared against <see cref="LowThreshold"/>.</summary>
    public decimal Quantity { get; set; }

    public string? Unit { get; set; }

    /// <summary>Null disables restock tracking for this item.</summary>
    public decimal? LowThreshold { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The code that was scanned to stock this, when there was one.</summary>
    public string? Barcode { get; set; }

    /// <summary>Stamped when a low-stock restock was appended to the list, cleared when the
    /// quantity climbs back above the threshold. This is the idempotency guard: without it every
    /// further decrement below the threshold would append a duplicate line to the shopping
    /// list.</summary>
    public DateTimeOffset? RestockedAt { get; set; }

    /// <summary>
    /// When the pantry was warned that this item is about to go off, or null if it has not been.
    /// </summary>
    public DateTimeOffset? ExpiryNotifiedAt { get; set; }

    /// <summary>
    /// When the house was told this item is running low, or null if it has not been.
    /// </summary>
    public DateTimeOffset? LowNotifiedAt { get; set; }

    public string AddedByUserId { get; set; } = null!;

    public static PantryItem Create(CreatePantryItemParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new PantryItem
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Name = @params.Name,
            Quantity = @params.Quantity,
            Unit = @params.Unit,
            LowThreshold = @params.LowThreshold,
            ExpiresAt = @params.ExpiresAt,
            Barcode = @params.Barcode,
            AddedByUserId = @params.AddedByUserId,
        };
    }

    /// <summary>True when this item has just crossed from "stocked" to "needs restocking" and
    /// hasn't already been put on the list.</summary>
    public bool NeedsRestock() =>
        LowThreshold is not null && Quantity <= LowThreshold && RestockedAt is null;

    /// <summary>True while the item is at or below its threshold, whatever has been done about it.
    /// <see cref="NeedsRestock"/> is this plus "and no list line exists yet".</summary>
    public bool IsLow() => LowThreshold is not null && Quantity <= LowThreshold;

    /// <summary>True when the house has not yet been told about this low episode.</summary>
    public bool NeedsLowAlert() => IsLow() && LowNotifiedAt is null;
}

/// <summary>Per-pantry-channel settings, upserted like ForumConfig - "the config" always exists
/// conceptually, defaulting to no linked list.</summary>
public class PantryConfig
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>The List channel low-stock items get appended to.</summary>
    public string? RestockListChannelId { get; set; }

    public int ExpiryWarningDays { get; set; } = 3;

    public DateTimeOffset UpdatedAt { get; set; }
}
