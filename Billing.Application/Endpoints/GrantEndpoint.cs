using System.Security.Claims;
using Billing.Application.Dtos;
using Billing.Application.Security;
using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Model;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>The staff grant surface of monetization.md section 6.</summary>
public class GrantEndpoint
{
    /// <summary>Every grant ever attached to a subject, newest first, with the subject's current
    /// entitlement version. Revoked and expired grants included unless <c>activeOnly</c> is set.
    /// </summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/grants/{subjectKind}/{subjectId}")]
    public static async Task<IResult> ListAsync(
        string subjectKind,
        string subjectId,
        bool activeOnly,
        [NotBody] GrantService grants,
        [NotBody] EntitlementVersionService versions,
        [NotBody] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!TrySubject(subjectKind, subjectId, out var subject, out var error)) return error;

        var rows = await grants.ListAsync(subject, activeOnly, cancellationToken);
        var version = await versions.VersionAsync(subject, cancellationToken);
        var now = clock.GetUtcNow();

        return Results.Ok(new GrantListDto(
            subject.Kind, subject.Id, version, rows.Select(row => GrantDto.From(row, now)).ToList()));
    }

    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/grants/{grantId}")]
    public static async Task<IResult> GetAsync(
        string grantId,
        [NotBody] GrantService grants,
        [NotBody] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var grant = await grants.FindAsync(grantId, cancellationToken);

        return grant is null
            ? Results.NotFound()
            : Results.Ok(GrantDto.From(grant, clock.GetUtcNow()));
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/grants")]
    public static async Task<(IResult, EntitlementsChanged?)> IssueAsync(
        IssueGrantRequest request,
        [NotBody] GrantService grants,
        [NotBody] TimeProvider clock,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<GrantEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), null);

        try
        {
            var (grant, announcement) = await grants.IssueAsync(
                new IssueGrant(
                    request.SubjectKind,
                    request.SubjectId,
                    request.GrantKind,
                    request.Plan,
                    request.Entitlements,
                    request.ExpiresAt,
                    request.Reason,
                    request.Source),
                actor,
                cancellationToken);

            logger.LogInformation(
                "Staff {Actor} granted {SubjectKind} {SubjectId} {What} until {Expiry}: {Reason}",
                actor, grant.SubjectKind, grant.SubjectId, grant.Plan ?? "specific entitlements",
                grant.ExpiresAt?.ToString("O") ?? "forever", grant.Reason);

            return (Results.Ok(GrantDto.From(grant, clock.GetUtcNow())), announcement);
        }
        catch (GrantRefusedException refusal)
        {
            return (Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message)), null);
        }
    }

    /// <summary>Ends a grant. The row stays, with who did it and why.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/grants/{grantId}/revoke")]
    public static async Task<(IResult, EntitlementsChanged?)> RevokeAsync(
        string grantId,
        RevokeGrantRequest request,
        [NotBody] GrantService grants,
        [NotBody] TimeProvider clock,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<GrantEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), null);

        try
        {
            var (grant, announcement) = await grants.RevokeAsync(
                grantId, actor, request.Reason, cancellationToken);

            logger.LogInformation(
                "Staff {Actor} revoked grant {GrantId} on {SubjectKind} {SubjectId}: {Reason}",
                actor, grant.Id, grant.SubjectKind, grant.SubjectId, request.Reason);

            return (Results.Ok(GrantDto.From(grant, clock.GetUtcNow())), announcement);
        }
        catch (KeyNotFoundException)
        {
            return (Results.NotFound(), null);
        }
        catch (GrantRefusedException refusal)
        {
            return (Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message)), null);
        }
    }

    /// <summary>Extends a grant, shortens it, or converts it to a permanent one by sending a null
    /// expiry. All three are operations section 6 lists for the console.</summary>
    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePatch("/api/v1/grants/{grantId}/expiry")]
    public static async Task<(IResult, EntitlementsChanged?)> AmendExpiryAsync(
        string grantId,
        AmendGrantExpiryRequest request,
        [NotBody] GrantService grants,
        [NotBody] TimeProvider clock,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<GrantEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return (Results.Unauthorized(), null);

        try
        {
            var (grant, announcement) = await grants.AmendExpiryAsync(
                grantId, request.ExpiresAt, cancellationToken);

            logger.LogInformation(
                "Staff {Actor} moved grant {GrantId} to expire {Expiry}",
                actor, grant.Id, grant.ExpiresAt?.ToString("O") ?? "never");

            return (Results.Ok(GrantDto.From(grant, clock.GetUtcNow())), announcement);
        }
        catch (KeyNotFoundException)
        {
            return (Results.NotFound(), null);
        }
        catch (GrantRefusedException refusal)
        {
            return (Results.BadRequest(new GrantErrorDto(refusal.Code, refusal.Message)), null);
        }
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
                GrantErrorCodes.SubjectRequired, "A subject id is required."));
            return false;
        }

        subject = new EntitlementSubject(kind, subjectId);
        error = Results.Empty;
        return true;
    }
}
