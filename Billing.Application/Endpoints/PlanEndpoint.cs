using System.Security.Claims;
using Billing.Application.Dtos;
using Billing.Application.Security;
using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Model;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>The plan catalogue, editable without a deploy (pricing model section 8).</summary>
public class PlanEndpoint
{
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/plans")]
    public static async Task<IResult> ListAsync(
        bool includeArchived,
        [NotBody] PlanService plans,
        CancellationToken cancellationToken)
    {
        var rows = await plans.ListAsync(includeArchived, cancellationToken);
        var dtos = new List<PlanDto>(rows.Count);

        foreach (var plan in rows)
        {
            dtos.Add(PlanDto.From(plan, await plans.VersionsAsync(plan.Id, cancellationToken)));
        }

        return Results.Ok(dtos);
    }

    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/plans/{name}")]
    public static async Task<IResult> GetAsync(
        string name,
        [NotBody] PlanService plans,
        CancellationToken cancellationToken)
    {
        var plan = await plans.FindAsync(name, cancellationToken);
        if (plan is null) return Results.NotFound();

        return Results.Ok(PlanDto.From(plan, await plans.VersionsAsync(plan.Id, cancellationToken)));
    }

    /// <summary>"This affects 1,240 guilds", for the console to show before an admin commits an
    /// edit. A read, so a Moderator may ask it.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/plans/{name}/blast-radius")]
    public static async Task<IResult> BlastRadiusAsync(
        string name,
        [NotBody] PlanService plans,
        CancellationToken cancellationToken)
    {
        var plan = await plans.FindAsync(name, cancellationToken);
        if (plan is null) return Results.NotFound();

        return Results.Ok(PlanBlastRadiusDto.From(await plans.BlastRadiusAsync(plan, cancellationToken)));
    }

    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/plans/{name}/audit")]
    public static async Task<IResult> AuditAsync(
        string name,
        [NotBody] PlanService plans,
        CancellationToken cancellationToken)
    {
        var plan = await plans.FindAsync(name, cancellationToken);
        if (plan is null) return Results.NotFound();

        var entries = await plans.AuditAsync(plan.Id, cancellationToken);

        return Results.Ok(entries.Select(PlanAuditEntryDto.From).ToList());
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/plans")]
    public static async Task<IResult> CreateAsync(
        CreatePlanRequest request,
        [NotBody] PlanService plans,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PlanEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            var (plan, version) = await plans.CreateAsync(
                new CreatePlan(request.Name, request.DisplayName, request.Description,
                    request.Values, request.PriceMinorUnits, request.Currency, request.Reason),
                actor, cancellationToken);

            logger.LogInformation("Staff {Actor} created plan {Plan}: {Reason}",
                actor, plan.Name, request.Reason);

            return Results.Ok(PlanDto.From(plan, [version]));
        }
        catch (PlanRefusedException refusal)
        {
            return Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message));
        }
    }

    /// <summary>The edit.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/plans/{name}/versions")]
    public static async Task<(IResult, OutgoingMessages)> EditAsync(
        string name,
        EditPlanRequest request,
        [NotBody] PlanService plans,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), []);

        try
        {
            var (version, announcements) = await plans.EditAsync(
                name,
                new EditPlan(request.Values, request.PriceMinorUnits, request.Currency,
                    request.Reason, request.DisplayName, request.Description),
                actor, cancellationToken);

            return (Results.Ok(PlanVersionDto.From(version, version.VersionNumber)),
                Cascade(announcements));
        }
        catch (PlanRefusedException refusal)
        {
            return (Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message)), []);
        }
    }

    /// <summary>Makes an existing version current again - the rollback path.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/plans/{name}/versions/{versionNumber}/activate")]
    public static async Task<(IResult, OutgoingMessages)> ActivateAsync(
        string name,
        int versionNumber,
        PlanReasonRequest request,
        [NotBody] PlanService plans,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), []);

        try
        {
            var (version, announcements) = await plans.ActivateAsync(
                name, versionNumber, request.Reason, actor, cancellationToken);

            return (Results.Ok(PlanVersionDto.From(version, versionNumber)), Cascade(announcements));
        }
        catch (PlanRefusedException refusal)
        {
            return (Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message)), []);
        }
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/plans/{name}/versions/{versionNumber}/archive")]
    public static async Task<IResult> ArchiveVersionAsync(
        string name,
        int versionNumber,
        PlanReasonRequest request,
        [NotBody] PlanService plans,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            var (plan, version) = await plans.ArchiveVersionAsync(
                name, versionNumber, request.Reason, actor, cancellationToken);

            return Results.Ok(PlanVersionDto.From(version, plan.CurrentVersionNumber));
        }
        catch (PlanRefusedException refusal)
        {
            return Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message));
        }
    }

    /// <summary>Stops a plan being sold.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/plans/{name}/archive")]
    public static async Task<IResult> ArchivePlanAsync(
        string name,
        PlanReasonRequest request,
        [NotBody] PlanService plans,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PlanEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            var plan = await plans.ArchivePlanAsync(name, request.Reason, actor, cancellationToken);

            logger.LogInformation("Staff {Actor} archived plan {Plan}: {Reason}",
                actor, plan.Name, request.Reason);

            return Results.Ok(PlanDto.From(plan, await plans.VersionsAsync(plan.Id, cancellationToken)));
        }
        catch (PlanRefusedException refusal)
        {
            return Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message));
        }
    }

    /// <summary>What plan and version a subject is on, or 404 for one that has never been assigned -
    /// which is most of them and is not a failure.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/plans/subjects/{subjectKind}/{subjectId}")]
    public static async Task<IResult> SubjectAsync(
        string subjectKind,
        string subjectId,
        [NotBody] PlanService plans,
        [NotBody] PlanCatalogueService catalogue,
        CancellationToken cancellationToken)
    {
        if (!TrySubject(subjectKind, subjectId, out var subject, out var error)) return error;

        var assignment = await catalogue.AssignmentForAsync(subject, cancellationToken);
        if (assignment is null) return Results.NotFound();

        var plan = await plans.FindByIdAsync(assignment.PlanId, cancellationToken);

        return plan is null
            ? Results.NotFound()
            : Results.Ok(PlanAssignmentDto.From(assignment, plan.Name));
    }

    /// <summary>Puts a subject on a plan version, or moves them between versions.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePut("/api/v1/plans/subjects/{subjectKind}/{subjectId}")]
    public static async Task<(IResult, EntitlementsChanged?)> AssignAsync(
        string subjectKind,
        string subjectId,
        AssignPlanRequest request,
        [NotBody] PlanService plans,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PlanEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), null);

        if (!TrySubject(subjectKind, subjectId, out var subject, out var error)) return (error, null);

        try
        {
            var (assignment, announcement) = await plans.AssignAsync(
                subject, request.Plan, request.VersionNumber, request.Reason, actor, cancellationToken);

            logger.LogInformation(
                "Staff {Actor} put {SubjectKind} {SubjectId} on {Plan} version {Version}: {Reason}",
                actor, subject.Kind, subject.Id, request.Plan, assignment.VersionNumber, request.Reason);

            return (Results.Ok(PlanAssignmentDto.From(assignment, request.Plan)), announcement);
        }
        catch (PlanRefusedException refusal)
        {
            return (Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message)), null);
        }
    }

    private static OutgoingMessages Cascade(IReadOnlyList<EntitlementsChanged> announcements)
    {
        var messages = new OutgoingMessages();
        foreach (var announcement in announcements) messages.Add(announcement);

        return messages;
    }

    private static string? ActorId(ClaimsPrincipal caller)
    {
        var actor = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(actor) ? null : actor;
    }

    private static bool TrySubject(
        string subjectKind, string subjectId, out EntitlementSubject subject, out IResult error)
    {
        subject = default;

        if (!Enum.TryParse<SubjectKind>(subjectKind, ignoreCase: true, out var kind))
        {
            error = Results.BadRequest(new GrantErrorDto(
                "unknown_subject_kind",
                $"'{subjectKind}' is not a subject kind. Known kinds: "
                + $"{string.Join(", ", Enum.GetNames<SubjectKind>())}."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            error = Results.BadRequest(new GrantErrorDto(
                PlanErrorCodes.SubjectRequired, "A subject id is required."));
            return false;
        }

        subject = new EntitlementSubject(kind, subjectId);
        error = Results.Empty;
        return true;
    }
}
