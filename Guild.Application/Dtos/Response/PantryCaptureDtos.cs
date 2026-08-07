namespace Guild.Application.Dtos.Response;

// Barcode lives on PantryItemDto in Dtos/Response/HouseholdDtos.cs, alongside the rest of the row.

/// <summary>What a scan did, which the client cannot infer from the item alone.</summary>
public class ScanPantryItemResultDto
{
    public required PantryItemDto Item { get; init; }
    public required bool Created { get; init; }
    public required bool Learned { get; init; }
}

/// <summary>One learned barcode, for a client offering completions.</summary>
public class PantryBarcodeDto
{
    public required string Barcode { get; init; }
    public required string Name { get; init; }
    public string? Unit { get; init; }
    public required decimal DefaultQuantity { get; init; }
    public decimal? LowThreshold { get; init; }
    public required int TimesSeen { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
}
