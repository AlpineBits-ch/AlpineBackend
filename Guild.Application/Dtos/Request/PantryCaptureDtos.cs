namespace Guild.Application.Dtos.Request;

// ── Pantry capture ───────────────────────────────────────────────────────────

/// <summary>A scan.</summary>
public class ScanPantryItemDto
{
    public string Barcode { get; set; } = null!;

    /// <summary>How much this scan adds.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Required only the first time a code is seen in this guild.</summary>
    public string? Name { get; set; }

    public string? Unit { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>"Used it up." The tap the module was missing - without it, eating the last yoghurt
/// meant opening a form and editing a decimal, which is why nobody did.</summary>
public class ConsumePantryItemDto
{
    /// <summary>Defaults to 1. Never takes the quantity below zero.</summary>
    public decimal? Amount { get; set; }

    /// <summary>Sets the quantity to zero outright, for the far commoner "that was the last of
    /// it" - which a client cannot express as an amount without first knowing the exact stock.</summary>
    public bool? All { get; set; }
}

/// <summary>"Put some back." Defaults to 1.</summary>
public class RestockPantryItemDto
{
    public decimal? Amount { get; set; }
}

// Barcode and ClearBarcode live on CreatePantryItemDto / UpdatePantryItemDto in
// Dtos/Request/HouseholdDtos.cs, alongside every other field of the same shape.
