using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateMaintenanceAssetParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Location { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public DateTimeOffset? PurchasedAt { get; set; }
    public DateTimeOffset? WarrantyUntil { get; set; }
    public string? VendorName { get; set; }
    public string? VendorPhone { get; set; }
    public string? VendorEmail { get; set; }
    public string? Notes { get; set; }
    public int? ServiceIntervalDays { get; set; }
    public DateTimeOffset? LastServicedAt { get; set; }
    public string AddedByUserId { get; set; } = null!;
}

/// <summary>
/// One thing in the house that needs looking after: the boiler, the washing machine, the smoke
/// alarms.
/// </summary>
public class MaintenanceAsset : BaseEntity<MaintenanceAsset>, IPrefixedEntity
{
    public static string Prefix { get; } = "asst";

    /// <summary>How far before a warranty lapses the house is warned.</summary>
    public const int WarrantyWarningDays = 30;

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Free text ("kitchen", "cellar") rather than a link to anything.</summary>
    public string? Location { get; set; }

    public string? Brand { get; set; }
    public string? Model { get; set; }

    /// <summary>The number a warranty claim or a spare-part order actually asks for, which is
    /// otherwise on a sticker behind the machine.</summary>
    public string? SerialNumber { get; set; }

    public DateTimeOffset? PurchasedAt { get; set; }

    public DateTimeOffset? WarrantyUntil { get; set; }

    public string? VendorName { get; set; }
    public string? VendorPhone { get; set; }
    public string? VendorEmail { get; set; }

    public string? Notes { get; set; }

    /// <summary>Null means nothing is scheduled for this asset - a sofa is worth cataloguing for
    /// its warranty and its receipt without ever being "due".</summary>
    public int? ServiceIntervalDays { get; set; }

    public DateTimeOffset? LastServicedAt { get; set; }

    /// <summary>When the next service falls due, or null when nothing is scheduled.</summary>
    public DateTimeOffset? NextServiceAt { get; set; }

    public AssetStatus Status { get; set; } = AssetStatus.Ok;

    /// <summary>Whatever the person who changed the status wanted the rest of the house to know
    /// ("leaks when you run a hot wash"). Free text; it is the sentence the notification and the
    /// board both show.</summary>
    public string? StatusNote { get; set; }

    /// <summary>
    /// When the house was told this is due for a service, or null if it has not been.
    /// </summary>
    public DateTimeOffset? ServiceNotifiedAt { get; set; }

    /// <summary>When the house was warned the warranty is about to lapse, or null if it has not
    /// been. Released when <see cref="WarrantyUntil"/> moves, so a corrected date warns again.</summary>
    public DateTimeOffset? WarrantyNotifiedAt { get; set; }

    public string AddedByUserId { get; set; } = null!;

    public static MaintenanceAsset Create(CreateMaintenanceAssetParams @params)
    {
        var date = DateTimeOffset.UtcNow;

        var asset = new MaintenanceAsset
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Name = @params.Name,
            Location = @params.Location,
            Brand = @params.Brand,
            Model = @params.Model,
            SerialNumber = @params.SerialNumber,
            PurchasedAt = @params.PurchasedAt,
            WarrantyUntil = @params.WarrantyUntil,
            VendorName = @params.VendorName,
            VendorPhone = @params.VendorPhone,
            VendorEmail = @params.VendorEmail,
            Notes = @params.Notes,
            ServiceIntervalDays = @params.ServiceIntervalDays,
            LastServicedAt = @params.LastServicedAt,
            AddedByUserId = @params.AddedByUserId,
        };

        asset.RescheduleService(date);
        return asset;
    }

    /// <summary>
    /// Records that the thing was actually serviced, and schedules the next one from <paramref
    /// name="performedAt"/>.
    /// </summary>
    public void RecordService(DateTimeOffset performedAt)
    {
        LastServicedAt = performedAt;
        RescheduleService(performedAt);

        // Released here rather than by the sweep, because the fact the last warning described -
        // "this is due" - has stopped being true.
        ServiceNotifiedAt = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Recomputes <see cref="NextServiceAt"/> from the interval, stepping off
    /// <paramref name="from"/> when there is nothing better to step off. An interval of null (or a
    /// nonsensical zero or negative one) clears the schedule outright rather than defaulting to
    /// something, because "nothing is scheduled" is a legitimate and common answer.</summary>
    public void RescheduleService(DateTimeOffset from)
    {
        NextServiceAt = ServiceIntervalDays is > 0
            ? (LastServicedAt ?? PurchasedAt ?? from).AddDays(ServiceIntervalDays.Value)
            : null;
    }

    /// <summary>True when a service was scheduled and that date has passed.</summary>
    public bool IsServiceOverdue(DateTimeOffset now) => NextServiceAt is { } due && due <= now;

    /// <summary>
    /// True when the warranty is inside its warning window and has not lapsed yet.
    /// </summary>
    public bool IsWarrantyExpiring(DateTimeOffset now) =>
        WarrantyUntil is { } until && until > now && until <= now.AddDays(WarrantyWarningDays);

    /// <summary>The instant a warranty warning becomes worth sending, or null when there is no
    /// warranty. Quiet hours are applied to this, not to the expiry date itself.</summary>
    public DateTimeOffset? WarrantyWarnAt => WarrantyUntil?.AddDays(-WarrantyWarningDays);

    /// <summary>True when this asset is asking for a human right now.</summary>
    public bool NeedsAttention(DateTimeOffset now) =>
        Status is AssetStatus.Broken or AssetStatus.NeedsAttention
        || IsServiceOverdue(now)
        || IsWarrantyExpiring(now);
}

public class CreateMaintenanceRecordParams
{
    public string? AssetId { get; set; }
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset PerformedAt { get; set; }
    public string PerformedByUserId { get; set; } = null!;
    public string? VendorName { get; set; }
    public long? CostMinor { get; set; }
    public string? Currency { get; set; }
    public string? ExpenseId { get; set; }
}

/// <summary>One thing that was done to the house: a service, a repair, a part replaced.</summary>
public class MaintenanceRecord : BaseEntity<MaintenanceRecord>, IPrefixedEntity
{
    public static string Prefix { get; } = "mrec";

    /// <summary>Nullable because a repair can exist without a catalogued asset.</summary>
    public string? AssetId { get; set; }

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    public DateTimeOffset PerformedAt { get; set; }
    public string PerformedByUserId { get; set; } = null!;

    public string? VendorName { get; set; }

    /// <summary>Minor units, like every other amount in the service.</summary>
    public long? CostMinor { get; set; }

    public string? Currency { get; set; }

    /// <summary>
    /// Points at a ledger <see cref="Expense"/> somebody already entered, or null.
    /// </summary>
    public string? ExpenseId { get; set; }

    public static MaintenanceRecord Create(CreateMaintenanceRecordParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new MaintenanceRecord
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            AssetId = @params.AssetId,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Title = @params.Title,
            Description = @params.Description,
            PerformedAt = @params.PerformedAt,
            PerformedByUserId = @params.PerformedByUserId,
            VendorName = @params.VendorName,
            CostMinor = @params.CostMinor,
            Currency = @params.Currency,
            ExpenseId = @params.ExpenseId,
        };
    }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<MaintenanceAsset>(assetBuilder =>
// {
//     assetBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithMany()
//         .HasForeignKey(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     assetBuilder.HasIndex(x => new { x.ChannelId, x.Name });
//
//     // Drives the guild-wide attention board across every maintenance channel.
//     assetBuilder.HasIndex(x => new { x.GuildId, x.Status });
//
//     // The service sweep's query: scheduled and not yet announced. Filtered so the index stays
//     // proportional to what is outstanding rather than to every asset in every house, most of
//     // which either has no schedule or has already been warned about.
//     assetBuilder.HasIndex(x => x.NextServiceAt)
//         .HasFilter("service_notified_at IS NULL AND next_service_at IS NOT NULL");
//
//     // The warranty sweep's query, filtered for the same reason.
//     assetBuilder.HasIndex(x => x.WarrantyUntil)
//         .HasFilter("warranty_notified_at IS NULL AND warranty_until IS NOT NULL");
// });
//
// modelBuilder.Entity<MaintenanceRecord>(recordBuilder =>
// {
//     recordBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithMany()
//         .HasForeignKey(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // Deleting a catalogued asset must not delete the history of what was done to it - the
//     // receipt for last year's boiler service is the reason the log exists. SetNull is the same
//     // call the invite-delete cascade fix made, for the same reason.
//     recordBuilder.HasOne<MaintenanceAsset>()
//         .WithMany()
//         .HasForeignKey(x => x.AssetId)
//         .IsRequired(false)
//         .OnDelete(DeleteBehavior.SetNull);
//
//     // The channel log, newest first - the keyset page order.
//     recordBuilder.HasIndex(x => new { x.ChannelId, x.PerformedAt });
//
//     // The per-asset history shown on an asset's own page.
//     recordBuilder.HasIndex(x => new { x.AssetId, x.PerformedAt });
// });
//
// DbSet: public DbSet<MaintenanceAsset> MaintenanceAssets { get; set; }
// DbSet: public DbSet<MaintenanceRecord> MaintenanceRecords { get; set; }
// MapEnum: options.MapEnum<AssetStatus>();
