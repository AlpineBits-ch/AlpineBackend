using Billing.Application.Services;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;

namespace Billing.Application.Dtos;

/// <summary>What the console posts to create a plan.</summary>
public sealed record CreatePlanRequest(
    string Name,
    string? DisplayName,
    string? Description,
    Dictionary<string, string> Values,
    long? PriceMinorUnits,
    string? Currency,
    string Reason);

/// <summary>An edit.</summary>
public sealed record EditPlanRequest(
    Dictionary<string, string> Values,
    long? PriceMinorUnits,
    string? Currency,
    string Reason,
    string? DisplayName = null,
    string? Description = null);

public sealed record PlanReasonRequest(string Reason);

/// <summary>Puts a subject on a plan.</summary>
public sealed record AssignPlanRequest(string Plan, int? VersionNumber, string Reason);

/// <summary>One version's numbers, in the same string form plan configuration and grants use.</summary>
public sealed record PlanVersionDto(
    int VersionNumber,
    bool IsCurrent,
    IReadOnlyDictionary<string, string> Values,
    long? PriceMinorUnits,
    string? Currency,
    string Reason,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    string? ArchivedBy,
    string? ArchiveReason)
{
    public static PlanVersionDto From(PlanVersion version, int currentVersionNumber)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new PlanVersionDto(
            version.VersionNumber,
            version.VersionNumber == currentVersionNumber,
            PlanCatalogueService.ReadValues(version.ValuesJson),
            version.PriceMinorUnits,
            version.Currency,
            version.Reason,
            version.CreatedBy,
            version.CreatedAt,
            version.ArchivedAt,
            version.ArchivedBy,
            version.ArchiveReason);
    }
}

/// <summary>A plan as the console reads it.</summary>
public sealed record PlanDto(
    string Name,
    string? DisplayName,
    string? Description,
    int CurrentVersionNumber,
    bool SeededFromConfiguration,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    string? ArchivedBy,
    string? ArchiveReason,
    IReadOnlyList<PlanVersionDto> Versions)
{
    public static PlanDto From(Plan plan, IReadOnlyList<PlanVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(versions);

        return new PlanDto(
            plan.Name,
            plan.DisplayName,
            plan.Description,
            plan.CurrentVersionNumber,
            plan.SeededFromConfiguration,
            plan.CreatedBy,
            plan.CreatedAt,
            plan.ArchivedAt,
            plan.ArchivedBy,
            plan.ArchiveReason,
            versions.Select(version => PlanVersionDto.From(version, plan.CurrentVersionNumber)).ToList());
    }
}

/// <summary>"This affects 1,240 guilds", before anybody presses save.</summary>
public sealed record PlanBlastRadiusDto(
    string Plan,
    int CurrentVersion,
    int TotalSubjects,
    IReadOnlyList<PlanVersionSubjectsDto> ByVersion,
    bool AppliesToEveryUnassignedSubject)
{
    public static PlanBlastRadiusDto From(PlanBlastRadius radius)
    {
        ArgumentNullException.ThrowIfNull(radius);

        return new PlanBlastRadiusDto(
            radius.Plan,
            radius.CurrentVersion,
            radius.TotalSubjects,
            radius.ByVersion
                .Select(entry => new PlanVersionSubjectsDto(entry.VersionNumber, entry.Subjects))
                .ToList(),
            radius.AppliesToEveryUnassignedSubject);
    }
}

public sealed record PlanVersionSubjectsDto(int VersionNumber, int Subjects);

public sealed record PlanAssignmentDto(
    SubjectKind SubjectKind,
    string SubjectId,
    string Plan,
    int VersionNumber,
    string AssignedBy,
    string Reason,
    DateTimeOffset AssignedAt)
{
    public static PlanAssignmentDto From(PlanAssignment assignment, string planName)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        return new PlanAssignmentDto(
            assignment.SubjectKind,
            assignment.SubjectId,
            planName,
            assignment.VersionNumber,
            assignment.AssignedBy,
            assignment.Reason,
            assignment.AssignedAt);
    }
}

/// <summary>One line of the plan audit trail.</summary>
public sealed record PlanAuditEntryDto(
    PlanChangeAction Action,
    int? VersionNumber,
    string Actor,
    string Reason,
    string? Subject,
    int? AffectedSubjects,
    DateTimeOffset OccurredAt)
{
    public static PlanAuditEntryDto From(PlanAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new PlanAuditEntryDto(
            entry.Action,
            entry.VersionNumber,
            entry.Actor,
            entry.Reason,
            entry.Subject,
            entry.AffectedSubjects,
            entry.OccurredAt);
    }
}
