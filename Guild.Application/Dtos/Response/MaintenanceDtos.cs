using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

public class MaintenanceAssetDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
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
    public DateTimeOffset? NextServiceAt { get; set; }
    public AssetStatus Status { get; set; }
    public string? StatusNote { get; set; }

    /// <summary>Computed rather than stored, so a client never has to know the sweep's cutoffs or
    /// carry a clock the server disagrees with.</summary>
    public bool IsServiceOverdue { get; set; }

    public bool IsWarrantyExpiring { get; set; }

    public string AddedByUserId { get; set; } = null!;
}

public class MaintenanceRecordDto
{
    public string Id { get; set; } = null!;
    public string? AssetId { get; set; }
    public string ChannelId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset PerformedAt { get; set; }
    public string PerformedByUserId { get; set; } = null!;
    public string? VendorName { get; set; }
    public long? CostMinor { get; set; }
    public string? Currency { get; set; }
    public string? ExpenseId { get; set; }
}

/// <summary>One row of the guild-wide attention board.</summary>
public class MaintenanceAttentionDto
{
    public MaintenanceAssetDto Asset { get; set; } = null!;

    /// <summary>Stable machine-readable tokens: <c>broken</c>, <c>needs_attention</c>,
    /// <c>service_overdue</c>, <c>warranty_expiring</c>. An asset can carry more than one.</summary>
    public List<string> Reasons { get; set; } = [];
}
