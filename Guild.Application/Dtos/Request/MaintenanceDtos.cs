using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

// ── Assets ───────────────────────────────────────────────────────────────────

public class CreateMaintenanceAssetDto
{
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

    /// <summary>Null leaves the asset unscheduled, which is the right answer for anything catalogued
    /// only for its warranty or its serial number.</summary>
    public int? ServiceIntervalDays { get; set; }

    /// <summary>When it was last serviced, if the house already knows.</summary>
    public DateTimeOffset? LastServicedAt { get; set; }
}

public class UpdateMaintenanceAssetDto
{
    public string? Name { get; set; }
    public string? Location { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public DateTimeOffset? PurchasedAt { get; set; }
    public bool? ClearPurchasedAt { get; set; }

    public DateTimeOffset? WarrantyUntil { get; set; }
    public bool? ClearWarrantyUntil { get; set; }

    public string? VendorName { get; set; }
    public string? VendorPhone { get; set; }
    public string? VendorEmail { get; set; }
    public string? Notes { get; set; }

    public int? ServiceIntervalDays { get; set; }

    /// <summary>Sent as an explicit flag because null means "leave the schedule alone" while
    /// switching scheduling off entirely has to be expressible too.</summary>
    public bool? ClearServiceInterval { get; set; }

    public DateTimeOffset? LastServicedAt { get; set; }
}

public class UpdateAssetStatusDto
{
    public AssetStatus Status { get; set; }

    /// <summary>What the person who found it wants the house to know.</summary>
    public string? Note { get; set; }
}

/// <summary>Marks an asset serviced and writes the log entry in one call, because they are one act
/// and splitting them means the half that gets skipped is always the log.</summary>
public class RecordServiceDto
{
    /// <summary>When the work was actually done.</summary>
    public DateTimeOffset? PerformedAt { get; set; }

    public string? Title { get; set; }
    public string? Notes { get; set; }
    public string? VendorName { get; set; }
    public long? CostMinor { get; set; }
    public string? Currency { get; set; }

    /// <summary>An existing ledger expense to link the cost to.</summary>
    public string? ExpenseId { get; set; }
}

// ── Records ──────────────────────────────────────────────────────────────────

public class CreateMaintenanceRecordDto
{
    /// <summary>Optional: a repair can be logged without a catalogued asset.</summary>
    public string? AssetId { get; set; }

    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset? PerformedAt { get; set; }
    public string? VendorName { get; set; }
    public long? CostMinor { get; set; }
    public string? Currency { get; set; }
    public string? ExpenseId { get; set; }
}

public class UpdateMaintenanceRecordDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? PerformedAt { get; set; }
    public string? VendorName { get; set; }

    public long? CostMinor { get; set; }
    public bool? ClearCost { get; set; }

    public string? Currency { get; set; }

    public string? ExpenseId { get; set; }
    public bool? ClearExpense { get; set; }
}
