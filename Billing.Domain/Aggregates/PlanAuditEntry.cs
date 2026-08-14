using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>What somebody did to the plan catalogue.</summary>
public enum PlanChangeAction
{
    PlanCreated,

    /// <summary>An edit: a new version was written.</summary>
    VersionCreated,

    /// <summary>A version became the plan's current one.</summary>
    VersionActivated,

    VersionArchived,

    PlanArchived,

    /// <summary>A subject was put on a plan version, or moved between versions.</summary>
    SubjectAssigned,
}

/// <summary>The plan catalogue's audit trail.</summary>
public class PlanAuditEntry : BaseEntity<PlanAuditEntry>, IPrefixedEntity
{
    public static string Prefix { get; } = "plad";

    public string PlanId { get; set; } = null!;

    /// <summary>The version the action concerned, where there was one.</summary>
    public int? VersionNumber { get; set; }

    public PlanChangeAction Action { get; set; }

    /// <summary>The staff account, or <c>system</c> for the configuration seed.</summary>
    public string Actor { get; set; } = null!;

    /// <summary>Required on every action that a person can take.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>The subject an assignment concerned, in <c>kind:id</c> form.</summary>
    public string? Subject { get; set; }

    /// <summary>How many subjects were on the plan when the action was taken - the blast radius as
    /// it actually was, rather than as it can be recomputed later once people have moved. Null where
    /// the action affected nobody but its own row.</summary>
    public int? AffectedSubjects { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
