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

/// <summary>
/// "This is what we call this code." The house stating a name for a barcode as its own act, with no
/// stock movement of any kind.
/// </summary>
public class TeachPantryBarcodeDto
{
    /// <summary>Required. There is no other field this request is really about.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Null means "leave whatever is there", not "clear it" - see
    /// <c>PantryBarcode.StateName</c>. A correction made from a scanner toast knows the name and
    /// nothing else.</summary>
    public string? Unit { get; set; }

    /// <summary>How much one scan of this code should add in future. Null leaves it alone.</summary>
    public decimal? DefaultQuantity { get; set; }
}

// Barcode and ClearBarcode live on CreatePantryItemDto / UpdatePantryItemDto in
// Dtos/Request/HouseholdDtos.cs, alongside every other field of the same shape.
