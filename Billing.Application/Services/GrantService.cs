using System.Text.Json;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Sources;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>Machine-readable refusals.</summary>
public static class GrantErrorCodes
{
    public const string ReasonRequired = "reason_required";
    public const string SubjectRequired = "subject_required";
    public const string ActorRequired = "actor_required";
    public const string PlanRequired = "plan_required";
    public const string UnknownPlan = "unknown_plan";
    public const string EntitlementsRequired = "entitlements_required";
    public const string UnknownEntitlementKey = "unknown_entitlement_key";
    public const string InvalidEntitlementValue = "invalid_entitlement_value";
    public const string ExpiryInThePast = "expiry_in_the_past";
    public const string AlreadyRevoked = "already_revoked";

    /// <summary>A start date at or after the expiry, which would be a grant that never counts for a
    /// single instant. Its own code rather than folded into <see cref="ExpiryInThePast"/>, because
    /// the two are fixed by editing different fields.</summary>
    public const string StartAfterExpiry = "start_after_expiry";
}

/// <summary>A grant was refused.</summary>
public sealed class GrantRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What a caller wants issued.</summary>
public sealed record IssueGrant(
    SubjectKind SubjectKind,
    string SubjectId,
    GrantKind GrantKind,
    string? Plan,
    IReadOnlyDictionary<string, string>? Entitlements,
    DateTimeOffset? ExpiresAt,
    string Reason,
    GrantSource Source,
    DateTimeOffset? StartsAt = null);

/// <summary>
/// Issuing, amending and revoking admin grants (monetization.md section 6), and answering the
/// resolver's question about them.
/// </summary>
public sealed class GrantService(
    MicroserviceContext db,
    PlanCatalogueService plans,
    EntitlementVersionService versions,
    TimeProvider clock) : IGrantProvider
{
    /// <summary>
    /// The plan table as it is now: the rows in this database, with the configured plans
    /// underneath.
    /// </summary>
    public Task<PlanCatalogue> CatalogueAsync(CancellationToken cancellationToken) =>
        plans.CurrentAsync(cancellationToken);

    /// <summary>Issues a grant, and returns it alongside the event that announces it.</summary>
    public async Task<(Grant Grant, EntitlementsChanged Event)> IssueAsync(
        IssueGrant request, string createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Required by monetization.md section 6, checked here as well as being NOT NULL in the
        // schema. The database refusal is the backstop; this one is the message an operator reads.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new GrantRefusedException(GrantErrorCodes.ReasonRequired,
                "A grant needs a reason. It is the whole value of keeping revoked grants: without one, "
                + "'who gave this guild Pro and why' has no answer a year later.");
        }

        if (string.IsNullOrWhiteSpace(request.SubjectId))
        {
            throw new GrantRefusedException(GrantErrorCodes.SubjectRequired,
                "A grant needs a subject to attach to.");
        }

        if (string.IsNullOrWhiteSpace(createdBy))
        {
            throw new GrantRefusedException(GrantErrorCodes.ActorRequired,
                "A grant records the staff account that issued it, and this request carried none.");
        }

        var now = clock.GetUtcNow();

        // A grant that has already lapsed grants nothing and looks, on the provenance screen,
        // exactly like one that was applied and then expired.
        if (request.ExpiresAt is { } expiry && expiry <= now)
        {
            throw new GrantRefusedException(GrantErrorCodes.ExpiryInThePast,
                $"The expiry {expiry:O} has already passed, so this grant would never take effect. "
                + "Leave it null for a permanent grant.");
        }

        // A future start is legitimate and is what a queued credit purchase is made of; a start at or
        // after the expiry is a grant that never counts for a single instant, which is the same
        // nothing an expiry in the past buys.
        if (request.StartsAt is { } start && request.ExpiresAt is { } end && start >= end)
        {
            throw new GrantRefusedException(GrantErrorCodes.StartAfterExpiry,
                $"This grant would start at {start:O} and expire at {end:O}, so it would never count "
                + "for a single instant.");
        }

        var changedKeys = await ValidateAsync(request, cancellationToken);

        var grant = new Grant
        {
            Id = Grant.GenerateId(),
            SubjectKind = request.SubjectKind,
            SubjectId = request.SubjectId,
            GrantKind = request.GrantKind,
            Plan = request.GrantKind == GrantKind.Plan ? request.Plan : null,
            EntitlementsJson = request.GrantKind == GrantKind.Entitlements
                ? JsonSerializer.Serialize(request.Entitlements)
                : null,
            ExpiresAt = request.ExpiresAt,
            StartsAt = request.StartsAt,
            Reason = request.Reason.Trim(),
            Source = request.Source,
            CreatedBy = createdBy,
        };

        db.Grants.Add(grant);

        return (grant, await AnnounceAsync(
            grant, EntitlementsChangedReason.GrantIssued, changedKeys, now, cancellationToken));
    }

    /// <summary>Ends a grant, keeping the row.</summary>
    public async Task<(Grant Grant, EntitlementsChanged Event)> RevokeAsync(
        string grantId, string revokedBy, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new GrantRefusedException(GrantErrorCodes.ReasonRequired,
                "A revocation needs a reason for the same reason the grant did.");
        }

        if (string.IsNullOrWhiteSpace(revokedBy))
        {
            throw new GrantRefusedException(GrantErrorCodes.ActorRequired,
                "A revocation records the staff account that made it, and this request carried none.");
        }

        var grant = await FindAsync(grantId, cancellationToken)
                    ?? throw new KeyNotFoundException($"No grant {grantId}.");

        if (grant.IsRevoked)
        {
            throw new GrantRefusedException(GrantErrorCodes.AlreadyRevoked,
                $"Grant {grantId} was revoked at {grant.RevokedAt:O}. Revoking it again would overwrite "
                + "who did it and why.");
        }

        var now = clock.GetUtcNow();
        grant.Revoke(revokedBy, reason.Trim(), now);

        return (grant, await AnnounceAsync(
            grant, EntitlementsChangedReason.GrantRevoked,
            await KeysOfAsync(grant, cancellationToken), now, cancellationToken));
    }

    /// <summary>Moves a grant's expiry, in either direction, or removes it entirely.</summary>
    public async Task<(Grant Grant, EntitlementsChanged Event)> AmendExpiryAsync(
        string grantId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var grant = await FindAsync(grantId, cancellationToken)
                    ?? throw new KeyNotFoundException($"No grant {grantId}.");

        if (grant.IsRevoked)
        {
            throw new GrantRefusedException(GrantErrorCodes.AlreadyRevoked,
                $"Grant {grantId} was revoked at {grant.RevokedAt:O} and cannot be amended. Issue a new "
                + "grant instead, so the record of what happened stays readable.");
        }

        var now = clock.GetUtcNow();

        if (expiresAt is { } expiry && expiry <= now)
        {
            throw new GrantRefusedException(GrantErrorCodes.ExpiryInThePast,
                $"The expiry {expiry:O} has already passed. To end a grant now, revoke it - that records "
                + "who did it and why, which backdating an expiry does not.");
        }

        grant.ExpiresAt = expiresAt;

        return (grant, await AnnounceAsync(
            grant, EntitlementsChangedReason.GrantAmended,
            await KeysOfAsync(grant, cancellationToken), now, cancellationToken));
    }

    public Task<Grant?> FindAsync(string grantId, CancellationToken cancellationToken) =>
        db.Grants.FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken);

    /// <summary>Every grant ever attached to a subject, newest first.</summary>
    public async Task<IReadOnlyList<Grant>> ListAsync(
        EntitlementSubject subject, bool activeOnly, CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;
        var now = clock.GetUtcNow();

        var query = db.Grants.Where(g => g.SubjectKind == kind && g.SubjectId == id);

        if (activeOnly)
        {
            query = query.Where(g => g.RevokedAt == null && (g.ExpiresAt == null || g.ExpiresAt > now));
        }

        return await query.OrderByDescending(g => g.CreatedAt).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The resolver's view: live grants only, in the shape <c>Echo.Entitlements</c> can consume.
    /// </summary>
    public async Task<IReadOnlyList<EntitlementGrant>> ActiveGrantsAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var live = await ListAsync(subject, activeOnly: true, cancellationToken);

        return live.Where(grant => grant.HasStartedAt(now)).Select(ToEntitlementGrant).ToList();
    }

    public static EntitlementGrant ToEntitlementGrant(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return new EntitlementGrant(
            grant.Id,
            grant.GrantKind == GrantKind.Plan ? grant.Plan : null,
            grant.GrantKind == GrantKind.Entitlements ? ReadEntitlements(grant.EntitlementsJson) : null,
            grant.ExpiresAt,
            grant.StartsAt);
    }

    /// <summary>The key names a grant touches, for the advisory <c>changedKeys</c> on the event. Best
    /// effort by design: a consumer that ignores it has to be correct anyway, so a plan that is no
    /// longer configured yielding an empty list is not a failure.</summary>
    public static IReadOnlyList<string> KeysOf(Grant grant, PlanCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(catalogue);

        if (grant.GrantKind == GrantKind.Plan)
        {
            return catalogue.Find(grant.Plan)?.Values.Keys.Select(key => key.Name).ToList() ?? [];
        }

        return ReadEntitlements(grant.EntitlementsJson)?.Keys.ToList() ?? [];
    }

    private async Task<IReadOnlyList<string>> KeysOfAsync(Grant grant, CancellationToken cancellationToken) =>
        KeysOf(grant, await CatalogueAsync(cancellationToken));

    private async Task<EntitlementsChanged> AnnounceAsync(
        Grant grant,
        EntitlementsChangedReason reason,
        IReadOnlyList<string> changedKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Advanced inside the same transaction as the write it describes, so a grant that fails to
        // commit does not leave a version telling every client to refetch something that did not
        // change.
        var version = await versions.AdvanceAsync(grant.Subject, cancellationToken);

        return new EntitlementsChanged
        {
            SubjectKind = grant.SubjectKind,
            SubjectId = grant.SubjectId,
            Reason = reason,
            GrantId = grant.Id,
            Version = version,
            ChangedKeys = [.. changedKeys],
            OccurredAt = now,
        };
    }

    /// <summary>
    /// Checks that the grant will actually grant something, and returns the keys it touches.
    /// </summary>
    private async Task<IReadOnlyList<string>> ValidateAsync(
        IssueGrant request, CancellationToken cancellationToken)
    {
        if (request.GrantKind == GrantKind.Plan)
        {
            if (string.IsNullOrWhiteSpace(request.Plan))
            {
                throw new GrantRefusedException(GrantErrorCodes.PlanRequired,
                    "A plan grant needs a plan name.");
            }

            var catalogue = await CatalogueAsync(cancellationToken);

            var plan = catalogue.Find(request.Plan);
            if (plan is null)
            {
                var known = catalogue.Plans.Select(p => p.Name).ToList();

                throw new GrantRefusedException(GrantErrorCodes.UnknownPlan, known.Count == 0
                    ? $"'{request.Plan}' cannot be granted because this instance has no plans at all. "
                      + "Create one from the admin console, or define them under the Entitlements:Plans "
                      + "configuration section, which is what the catalogue is seeded from; until then "
                      + "a plan grant would be a row that grants nothing."
                    : $"'{request.Plan}' is not a plan this instance has. Known plans: "
                      + $"{string.Join(", ", known)}.");
            }

            return plan.Values.Keys.Select(key => key.Name).ToList();
        }

        if (request.Entitlements is not { Count: > 0 })
        {
            throw new GrantRefusedException(GrantErrorCodes.EntitlementsRequired,
                "An entitlements grant needs at least one key. A grant that grants nothing is a row "
                + "nobody can explain later.");
        }

        var names = new List<string>(request.Entitlements.Count);

        foreach (var (name, text) in request.Entitlements)
        {
            if (!EntitlementKeys.TryGet(name, out var key))
            {
                throw new GrantRefusedException(GrantErrorCodes.UnknownEntitlementKey,
                    $"'{name}' is not an entitlement key. Known keys: "
                    + $"{string.Join(", ", EntitlementKeys.All.Select(k => k.Name))}.");
            }

            try
            {
                key.Parse(text ?? string.Empty);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new GrantRefusedException(GrantErrorCodes.InvalidEntitlementValue,
                    $"'{text}' is not a value '{name}' can take: {exception.Message}");
            }

            names.Add(key.Name);
        }

        return names;
    }

    private static IReadOnlyDictionary<string, string>? ReadEntitlements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            // Unreachable for anything this service wrote.
            return null;
        }
    }
}
