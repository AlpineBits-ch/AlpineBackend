namespace Guild.Application.Dtos.Response;

// Barcode lives on PantryItemDto in Dtos/Response/HouseholdDtos.cs, alongside the rest of the row.

/// <summary>What a scan did, which the client cannot infer from the item alone.</summary>
public class ScanPantryItemResultDto
{
    public required PantryItemDto Item { get; init; }
    public required bool Created { get; init; }

    /// <summary>
    /// The house had never seen this code and now has: a <c>PantryBarcode</c> row was written from
    /// a name somebody stated.
    /// </summary>
    public required bool Learned { get; init; }

    /// <summary>Set when the shared product catalog supplied the name, null in every other case -
    /// including when the house's own learned name won, which it always does when there is
    /// one.</summary>
    public ProductCatalogMatchDto? Catalog { get; init; }
}

/// <summary>What the shared catalog said, and who to credit for it.</summary>
public class ProductCatalogMatchDto
{
    public required string Name { get; init; }

    /// <summary>Which language column the name actually came from, which is not necessarily the one
    /// asked for: a French-speaking flat scanning a product the source only has in German gets the
    /// German name and this says so. Source data also files names under the wrong language often
    /// enough to matter, so this is what we claim rather than a guarantee.</summary>
    public required string Language { get; init; }

    public string? Brand { get; init; }
    public decimal? Quantity { get; init; }
    public string? QuantityUnit { get; init; }

    /// <summary>Stable identifier of the database, for a client that wants to branch on it.</summary>
    public required string Source { get; init; }

    public required string SourceName { get; init; }

    /// <summary>The source's page for this specific product.</summary>
    public required string SourceUrl { get; init; }

    public required string License { get; init; }
    public required string LicenseUrl { get; init; }

    /// <summary>Ready-to-render notice. ODbL 4.3 states this wording is sufficient.</summary>
    public required string Attribution { get; init; }

    /// <summary>When this row entered our copy of the catalog, so a client can say how old the
    /// answer is instead of implying it is live.</summary>
    public required DateTimeOffset ImportedAt { get; init; }
}

/// <summary>What the ODbL 4.6 export is and where to get it.</summary>
public class ProductCatalogInfoDto
{
    public required string Source { get; init; }
    public required string SourceName { get; init; }
    public required string SourceUrl { get; init; }
    public required string License { get; init; }
    public required string LicenseUrl { get; init; }
    public required string Attribution { get; init; }

    /// <summary>Rows currently in the derived table.</summary>
    public required int Count { get; init; }

    /// <summary>The most recent import stamp on any row, or null on an empty catalog.</summary>
    public DateTimeOffset? LastImportedAt { get; init; }

    /// <summary>The extract tags present, newest import first.</summary>
    public required IReadOnlyList<string> SourceVersions { get; init; }

    public required string ExportUrl { get; init; }
    public required string ExportContentType { get; init; }

    /// <summary>Says in words what the export does and does not contain, so nobody has to infer the
    /// licence boundary from the field list.</summary>
    public required string Notice { get; init; }
}

/// <summary>The outcome of one bulk import, for the operator who ran it.</summary>
public class ProductCatalogImportResultDto
{
    public required string SourceVersion { get; init; }
    public required int Read { get; init; }
    public required int Created { get; init; }
    public required int Updated { get; init; }

    /// <summary>Rows that parsed but carried nothing worth storing: no barcode, or no name in any
    /// of the four languages. Counted rather than failed, because a 100,000-line extract with a
    /// hundred empty rows in it is a normal extract.</summary>
    public required int Skipped { get; init; }

    /// <summary>Lines that were not valid JSON. A non-zero count here usually means the wrong file.</summary>
    public required int Malformed { get; init; }

    /// <summary>Miss rows deleted because the import finally answered them.</summary>
    public required int MissesResolved { get; init; }
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
