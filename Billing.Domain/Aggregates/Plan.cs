using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>A sellable bundle, by name, with its history of versions.</summary>
public class Plan : BaseEntity<Plan>, IPrefixedEntity
{
    public static string Prefix { get; } = "plan";

    /// <summary>The stable key.</summary>
    public string Name { get; set; } = null!;

    /// <summary>What the console and, later, the checkout screen call it.</summary>
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>The version a subject joining this plan now gets, and the one a plan grant naming
    /// the bare plan name resolves through. Held here rather than as a flag on the version so that
    /// "which one is current" has exactly one answer and cannot be true twice.</summary>
    public int CurrentVersionNumber { get; set; }

    /// <summary>True when the plan arrived from configuration rather than from a person.</summary>
    public bool SeededFromConfiguration { get; set; }

    /// <summary>The staff account that created it, or <c>system</c> for a seeded plan.</summary>
    public string CreatedBy { get; set; } = null!;

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? ArchivedBy { get; set; }

    public string? ArchiveReason { get; set; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>Ends a plan without ending the record of it.</summary>
    public void Archive(string archivedBy, string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (IsArchived)
        {
            throw new InvalidOperationException(
                $"Plan {Name} was already archived at {ArchivedAt:O}. Archiving it again would "
                + "overwrite who did it and why.");
        }

        ArchivedAt = at;
        ArchivedBy = archivedBy;
        ArchiveReason = reason;
    }
}

/// <summary>One immutable set of numbers for a plan.</summary>
public class PlanVersion : BaseEntity<PlanVersion>, IPrefixedEntity
{
    public static string Prefix { get; } = "plnv";

    public string PlanId { get; set; } = null!;

    /// <summary>1-based and dense.</summary>
    public int VersionNumber { get; set; }

    public string ValuesJson { get; set; } = null!;

    /// <summary>
    /// The recurring price in the currency's minor units, or null for a plan that is not sold.
    /// </summary>
    public long? PriceMinorUnits { get; set; }

    /// <summary>ISO 4217, lowercase, matching Stripe's convention so the eventual price object does
    /// not need translating.</summary>
    public string? Currency { get; set; }

    /// <summary>Required, free text, and the reason this table is worth keeping.</summary>
    public string Reason { get; set; } = null!;

    public string CreatedBy { get; set; } = null!;

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? ArchivedBy { get; set; }

    public string? ArchiveReason { get; set; }

    public bool IsArchived => ArchivedAt is not null;
}
