using System.Text.Json;
using AppEnvironment;
using Echo.Domain.Entities.Moderation;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Echo.Billing;

/// <summary>
/// The console's billing section: grants, the provenance screen, the plan editor, and a forced
/// cache invalidation (monetization.md section 6, pricing model section 8).
/// </summary>
[Authorize]
[Route("api/v1/admin/billing")]
public class AdminBillingController(
    MicroserviceContext context,
    StaffAccess staff,
    BillingServiceClient billing,
    EntitlementResolver resolver,
    IEntitlementVersionProvider versions,
    EntitlementCacheInvalidator invalidator,
    ILogger<AdminBillingController> logger)
    : AdminControllerBase(context, staff)
{
    /// <summary>
    /// The floor <c>PlanService.MinimumVoiceParticipants</c> enforces, restated for the editor.
    /// </summary>
    private const long MinimumVoiceParticipants = 2;

    /// <summary>
    /// Mirrors <c>Billing.Domain.Aggregates.GrantKind</c> and <c>GrantSource</c>.
    /// </summary>
    private static readonly string[] GrantKinds = ["Plan", "Entitlements"];

    private static readonly string[] GrantSources = ["Staff", "Promotion", "Boost", "Migration"];

    // ── Catalogue ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the section needs before it draws anything: the entitlement key table, the enums, and
    /// whether this instance sells anything at all.
    /// </summary>
    [HttpGet("catalogue")]
    public async Task<IActionResult> CatalogueAsync()
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Ok(new BillingConsoleCatalogueDto(
            BillingServiceClient.IsDeployed,
            Env.License.Mode.Trim().ToLowerInvariant(),
            actor.IsAdmin,
            MinimumVoiceParticipants,
            [.. EntitlementKeys.All.Select(EntitlementKeyDto.From)],
            [.. Enum.GetNames<SubjectKind>()],
            GrantKinds,
            GrantSources));
    }

    // ── Provenance ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every effective key for one subject, its resolved value, and which source won it.
    /// </summary>
    [HttpGet("provenance/{subjectKind}/{subjectId}")]
    public async Task<IActionResult> ProvenanceAsync(string subjectKind, string subjectId, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        if (!TrySubject(subjectKind, subjectId, out var subject, out var refusal)) return refusal;

        return Ok(await ResolveProvenanceAsync(subject, ct));
    }

    private async Task<EntitlementProvenanceDto> ResolveProvenanceAsync(
        EntitlementSubject subject, CancellationToken ct)
    {
        var set = await resolver.ResolveAsync(subject, ct);

        // Asked separately and allowed to fail.
        long? version = null;

        try
        {
            version = await versions.VersionAsync(subject, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "The entitlement version for {Subject} could not be read; the provenance screen is "
                + "answering without it.", subject);
        }

        var entries = EntitlementKeys.For(subject.Kind)
            .OrderBy(key => key.Name, StringComparer.Ordinal)
            .Select(key => EntitlementProvenanceEntryDto.From(key, set))
            .ToList();

        return new EntitlementProvenanceDto(
            subject.Kind.ToString(),
            subject.Id,
            version,
            version is not null,
            Env.License.Mode.Trim().ToLowerInvariant(),
            BillingServiceClient.IsDeployed,
            DateTimeOffset.UtcNow,
            entries);
    }

    // ── Grants ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every grant ever attached to a subject, revoked and expired ones included. There is
    /// no delete on this surface anywhere, and the list is the reason: a revoked grant is still the
    /// answer to "who gave this guild Pro and why".</summary>
    [HttpGet("grants/{subjectKind}/{subjectId}")]
    public async Task<IActionResult> GrantsAsync(
        string subjectKind, string subjectId, [FromQuery] bool activeOnly, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        if (!TrySubject(subjectKind, subjectId, out var subject, out var refusal)) return refusal;

        return Forward(await billing.GetAsync(HttpContext,
            $"/api/v1/grants/{subject.Kind}/{Escape(subject.Id)}?activeOnly={(activeOnly ? "true" : "false")}", ct));
    }

    [HttpPost("grants")]
    public async Task<IActionResult> IssueGrantAsync([FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var subjectId = Text(request, "subjectId");

        return await WriteAsync(actor, HttpMethod.Post, "/api/v1/grants", request, ct,
            ModerationAuditActions.BillingGrantIssued, subjectId,
            $"{Text(request, "subjectKind")} {subjectId}: "
            + $"{Text(request, "plan") ?? "specific entitlements"} - {Text(request, "reason")}");
    }

    /// <summary>Ends a grant.</summary>
    [HttpPost("grants/{grantId}/revoke")]
    public async Task<IActionResult> RevokeGrantAsync(
        string grantId, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        return await WriteAsync(actor, HttpMethod.Post, $"/api/v1/grants/{Escape(grantId)}/revoke", request, ct,
            ModerationAuditActions.BillingGrantRevoked, grantId, Text(request, "reason"));
    }

    /// <summary>Extends a grant, shortens it, or converts it to a permanent one by sending a null
    /// expiry.</summary>
    [HttpPatch("grants/{grantId}/expiry")]
    public async Task<IActionResult> AmendGrantExpiryAsync(
        string grantId, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        return await WriteAsync(actor, HttpMethod.Patch, $"/api/v1/grants/{Escape(grantId)}/expiry", request, ct,
            ModerationAuditActions.BillingGrantAmended, grantId,
            $"expires {Text(request, "expiresAt") ?? "never"}");
    }

    // ── Plans ─────────────────────────────────────────────────────────────────────────────────

    [HttpGet("plans")]
    public async Task<IActionResult> PlansAsync([FromQuery] bool includeArchived, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Forward(await billing.GetAsync(HttpContext,
            $"/api/v1/plans?includeArchived={(includeArchived ? "true" : "false")}", ct));
    }

    [HttpGet("plans/{name}")]
    public async Task<IActionResult> PlanAsync(string name, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Forward(await billing.GetAsync(HttpContext, $"/api/v1/plans/{Escape(name)}", ct));
    }

    /// <summary>"This affects 1,240 guilds", split by the version each of them is on.</summary>
    [HttpGet("plans/{name}/blast-radius")]
    public async Task<IActionResult> BlastRadiusAsync(string name, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Forward(await billing.GetAsync(HttpContext, $"/api/v1/plans/{Escape(name)}/blast-radius", ct));
    }

    [HttpGet("plans/{name}/audit")]
    public async Task<IActionResult> PlanAuditAsync(string name, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Forward(await billing.GetAsync(HttpContext, $"/api/v1/plans/{Escape(name)}/audit", ct));
    }

    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlanAsync([FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var name = Text(request, "name");

        return await WriteAsync(actor, HttpMethod.Post, "/api/v1/plans", request, ct,
            ModerationAuditActions.BillingPlanCreated, name, Text(request, "reason"));
    }

    /// <summary>The edit, which writes a new version rather than changing one.</summary>
    [HttpPost("plans/{name}/versions")]
    public async Task<IActionResult> EditPlanAsync(
        string name, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        return await WriteAsync(actor, HttpMethod.Post, $"/api/v1/plans/{Escape(name)}/versions", request, ct,
            ModerationAuditActions.BillingPlanEdited, name, Text(request, "reason"));
    }

    [HttpPost("plans/{name}/versions/{versionNumber:int}/activate")]
    public async Task<IActionResult> ActivateVersionAsync(
        string name, int versionNumber, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        return await WriteAsync(actor, HttpMethod.Post,
            $"/api/v1/plans/{Escape(name)}/versions/{versionNumber}/activate", request, ct,
            ModerationAuditActions.BillingPlanVersionActivated, name,
            $"version {versionNumber}: {Text(request, "reason")}");
    }

    [HttpPost("plans/{name}/versions/{versionNumber:int}/archive")]
    public async Task<IActionResult> ArchiveVersionAsync(
        string name, int versionNumber, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        return await WriteAsync(actor, HttpMethod.Post,
            $"/api/v1/plans/{Escape(name)}/versions/{versionNumber}/archive", request, ct,
            ModerationAuditActions.BillingPlanVersionArchived, name,
            $"version {versionNumber}: {Text(request, "reason")}");
    }

    [HttpPost("plans/{name}/archive")]
    public async Task<IActionResult> ArchivePlanAsync(
        string name, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        return await WriteAsync(actor, HttpMethod.Post, $"/api/v1/plans/{Escape(name)}/archive", request, ct,
            ModerationAuditActions.BillingPlanArchived, name, Text(request, "reason"));
    }

    /// <summary>What plan and version a subject is on.</summary>
    [HttpGet("plans/subjects/{subjectKind}/{subjectId}")]
    public async Task<IActionResult> PlanAssignmentAsync(
        string subjectKind, string subjectId, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        if (!TrySubject(subjectKind, subjectId, out var subject, out var refusal)) return refusal;

        return Forward(await billing.GetAsync(HttpContext,
            $"/api/v1/plans/subjects/{subject.Kind}/{Escape(subject.Id)}", ct));
    }

    /// <summary>Puts a subject on a plan version, or moves them between versions.</summary>
    [HttpPut("plans/subjects/{subjectKind}/{subjectId}")]
    public async Task<IActionResult> AssignPlanAsync(
        string subjectKind, string subjectId, [FromBody] JsonElement request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        if (!TrySubject(subjectKind, subjectId, out var subject, out var refusal)) return refusal;

        return await WriteAsync(actor, HttpMethod.Put,
            $"/api/v1/plans/subjects/{subject.Kind}/{Escape(subject.Id)}", request, ct,
            ModerationAuditActions.BillingPlanAssigned, subject.Id,
            $"{Text(request, "plan")} version {Text(request, "versionNumber") ?? "current"}: "
            + $"{Text(request, "reason")}");
    }

    // ── Cache ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forces one subject's cached entitlements to be re-resolved, and answers with what they came
    /// out as.
    /// </summary>
    [HttpPost("cache/{subjectKind}/{subjectId}/invalidate")]
    public async Task<IActionResult> InvalidateAsync(string subjectKind, string subjectId, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        if (!TrySubject(subjectKind, subjectId, out var subject, out var refusal)) return refusal;

        await invalidator.InvalidateAsync(subject, ct);

        Audit(actor, ModerationAuditActions.BillingCacheInvalidated, subject.Id, subject.Kind.ToString());
        await Db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Staff {Actor} forced an entitlement cache invalidation for {Subject}.", actor.UserId, subject);

        // Re-resolved on the way out, so the screen the operator is looking at updates to whatever
        // the sources actually say rather than to what it had a moment ago.
        return Ok(await ResolveProvenanceAsync(subject, ct));
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Forwards a mutation, then records it here if Billing accepted it.</summary>
    private async Task<IActionResult> WriteAsync(
        StaffPrincipal actor,
        HttpMethod method,
        string path,
        JsonElement request,
        CancellationToken ct,
        string action,
        string? subjectId,
        string? detail)
    {
        // Every write on this surface carries a reason, so a bodyless request is a client bug
        // rather than a shorthand.
        if (request.ValueKind is JsonValueKind.Undefined)
        {
            return Failure(400, "body_required", "This request needs a JSON body with a reason in it.");
        }

        var reply = await billing.SendAsync(HttpContext, method, path, request.GetRawText(), ct);

        if (!reply.IsSuccess) return Forward(reply);

        Audit(actor, action, subjectId, detail);
        await Db.SaveChangesAsync(ct);

        logger.LogInformation("Staff {Actor} performed {Action} on {Subject}: {Detail}",
            actor.UserId, action, subjectId ?? "-", detail ?? "-");

        return Forward(reply);
    }

    /// <summary>Billing's answer, unaltered.</summary>
    private static IActionResult Forward(BillingReply reply) => new ContentResult
    {
        StatusCode = reply.Status,
        Content = reply.Body,
        ContentType = reply.Body is null ? null : "application/json",
    };

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>A property of the posted body as text, for the audit detail.</summary>
    private static string? Text(JsonElement body, string property)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.ToString(),
        };
    }

    private bool TrySubject(
        string subjectKind, string subjectId, out EntitlementSubject subject, out IActionResult refusal)
    {
        subject = default;

        if (!Enum.TryParse<SubjectKind>(subjectKind, ignoreCase: true, out var kind))
        {
            refusal = Failure(400, "unknown_subject_kind",
                $"'{subjectKind}' is not a subject kind. Known kinds: "
                + $"{string.Join(", ", Enum.GetNames<SubjectKind>())}.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            refusal = Failure(400, "subject_required", "A subject id is required.");
            return false;
        }

        subject = new EntitlementSubject(kind, subjectId);
        refusal = Ok();
        return true;
    }
}
