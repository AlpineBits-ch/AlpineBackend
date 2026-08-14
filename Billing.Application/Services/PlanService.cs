using System.Text.Json;
using Billing.Application.Stripe;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>Machine-readable refusals, closed set, one rendered sentence each in the console. Same
/// shape as <see cref="GrantErrorCodes"/> so one error renderer covers both surfaces.</summary>
public static class PlanErrorCodes
{
    public const string ReasonRequired = "reason_required";
    public const string ActorRequired = "actor_required";
    public const string PlanNameRequired = "plan_name_required";
    public const string InvalidPlanName = "invalid_plan_name";
    public const string PlanExists = "plan_exists";
    public const string UnknownPlan = "unknown_plan";
    public const string PlanArchived = "plan_archived";
    public const string PlanInUse = "plan_in_use";
    public const string ValuesRequired = "values_required";
    public const string UnknownEntitlementKey = "unknown_entitlement_key";
    public const string InvalidEntitlementValue = "invalid_entitlement_value";

    /// <summary>The one an admin cannot argue with: the plan tried to take away moderation, AutoMod
    /// or voice channels. See <see cref="PlanService.MinimumVoiceParticipants"/>.</summary>
    public const string WithholdsPlanIndependentModule = "withholds_plan_independent_module";

    public const string InvalidPrice = "invalid_price";
    public const string UnknownVersion = "unknown_version";
    public const string VersionArchived = "version_archived";
    public const string VersionInUse = "version_in_use";
    public const string AlreadyArchived = "already_archived";
    public const string SubjectRequired = "subject_required";

    /// <summary>The plan was fine and Stripe was not.</summary>
    public const string StripeSyncFailed = "stripe_sync_failed";
}

/// <summary>A plan change was refused.</summary>
public sealed class PlanRefusedException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>What the console posts to create a plan.</summary>
public sealed record CreatePlan(
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyDictionary<string, string> Values,
    long? PriceMinorUnits,
    string? Currency,
    string Reason);

/// <summary>An edit.</summary>
public sealed record EditPlan(
    IReadOnlyDictionary<string, string> Values,
    long? PriceMinorUnits,
    string? Currency,
    string Reason,
    string? DisplayName = null,
    string? Description = null);

/// <summary>How many subjects an edit would reach, per version.</summary>
public sealed record PlanBlastRadius(
    string Plan,
    int CurrentVersion,
    int TotalSubjects,
    IReadOnlyList<PlanVersionSubjects> ByVersion,
    bool AppliesToEveryUnassignedSubject);

public sealed record PlanVersionSubjects(int VersionNumber, int Subjects);

/// <summary>
/// The plan catalogue's write side: create a plan, edit it into a new version, roll one back,
/// archive, and move a subject between versions.
/// </summary>
public sealed class PlanService(
    MicroserviceContext db,
    PlanCatalogueService catalogue,
    EntitlementVersionService versions,
    EntitlementPlanOptions options,
    TimeProvider clock,
    StripeCatalogueSync? stripe = null,
    ILogger<PlanService>? logger = null)
{
    /// <summary>
    /// The floor under <c>voice.max_participants</c>, and the plan editor's half of
    /// <c>GuildFeatureMap.PlanIndependentFeatures</c>.
    /// </summary>
    public const long MinimumVoiceParticipants = 2;

    /// <summary>How many subjects one activation will announce individually.</summary>
    public const int AnnouncementLimit = 5000;

    public Task<Plan?> FindAsync(string name, CancellationToken cancellationToken) =>
        db.Plans.FirstOrDefaultAsync(plan => plan.Name == name, cancellationToken);

    public Task<Plan?> FindByIdAsync(string planId, CancellationToken cancellationToken) =>
        db.Plans.FirstOrDefaultAsync(plan => plan.Id == planId, cancellationToken);

    public async Task<IReadOnlyList<Plan>> ListAsync(bool includeArchived, CancellationToken cancellationToken)
    {
        var query = db.Plans.AsQueryable();
        if (!includeArchived) query = query.Where(plan => plan.ArchivedAt == null);

        return await query.OrderBy(plan => plan.Name).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlanVersion>> VersionsAsync(
        string planId, CancellationToken cancellationToken) =>
        await db.PlanVersions
            .Where(version => version.PlanId == planId)
            .OrderBy(version => version.VersionNumber)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlanAuditEntry>> AuditAsync(
        string planId, CancellationToken cancellationToken) =>
        await db.PlanAuditEntries
            .Where(entry => entry.PlanId == planId)
            .OrderByDescending(entry => entry.OccurredAt)
            .ToListAsync(cancellationToken);

    /// <summary>Creates a plan and its first version.</summary>
    public async Task<(Plan Plan, PlanVersion Version)> CreateAsync(
        CreatePlan request, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireActor(actor);
        RequireReason(request.Reason);
        var name = NormaliseName(request.Name);

        if (await db.Plans.AnyAsync(plan => plan.Name == name, cancellationToken))
        {
            throw new PlanRefusedException(PlanErrorCodes.PlanExists,
                $"A plan called '{name}' already exists. Edit it - which writes a new version and "
                + "leaves everyone on it where they are - rather than creating a second one under a "
                + "name that means the same thing.");
        }

        var values = Validate(request.Values);
        ValidatePrice(request.PriceMinorUnits, request.Currency);

        var now = clock.GetUtcNow();

        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CurrentVersionNumber = 1,
            SeededFromConfiguration = false,
            CreatedBy = actor,
        };

        var version = NewVersion(plan, 1, values, request.PriceMinorUnits, request.Currency,
            request.Reason, actor);

        db.Plans.Add(plan);
        db.PlanVersions.Add(version);

        await MirrorToStripeAsync(plan, version, cancellationToken);

        Record(plan, 1, PlanChangeAction.PlanCreated, actor, request.Reason, now, affected: 0);

        catalogue.Invalidate();

        return (plan, version);
    }

    /// <summary>The edit.</summary>
    public async Task<(PlanVersion Version, IReadOnlyList<EntitlementsChanged> Announcements)> EditAsync(
        string name, EditPlan request, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireActor(actor);
        RequireReason(request.Reason);

        var plan = await RequirePlanAsync(name, cancellationToken);
        var values = Validate(request.Values);
        ValidatePrice(request.PriceMinorUnits, request.Currency);

        var now = clock.GetUtcNow();
        var next = await NextVersionNumberAsync(plan.Id, cancellationToken);

        var version = NewVersion(plan, next, values, request.PriceMinorUnits, request.Currency,
            request.Reason, actor);

        db.PlanVersions.Add(version);
        plan.CurrentVersionNumber = next;

        if (!string.IsNullOrWhiteSpace(request.DisplayName)) plan.DisplayName = request.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(request.Description)) plan.Description = request.Description.Trim();

        // Before the announcements rather than after, so a Stripe failure throws while the only
        // thing that has happened is a change tracker nothing will commit.
        await MirrorToStripeAsync(plan, version, cancellationToken);

        var announcements = await AnnounceAsync(
            plan, values.Keys.Select(key => key.Name).ToList(), now, cancellationToken);

        Record(plan, next, PlanChangeAction.VersionCreated, actor, request.Reason, now,
            announcements.Count);

        catalogue.Invalidate();

        logger?.LogInformation(
            "Plan {Plan} edited to version {Version} by {Actor}, affecting {Subjects} subject(s): {Reason}",
            plan.Name, next, actor, announcements.Count, request.Reason);

        return (version, announcements);
    }

    /// <summary>Makes an existing version current again.</summary>
    public async Task<(PlanVersion Version, IReadOnlyList<EntitlementsChanged> Announcements)> ActivateAsync(
        string name, int versionNumber, string reason, string actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        RequireReason(reason);

        var plan = await RequirePlanAsync(name, cancellationToken);
        var version = await RequireVersionAsync(plan, versionNumber, cancellationToken);

        if (version.IsArchived)
        {
            throw new PlanRefusedException(PlanErrorCodes.VersionArchived,
                $"Version {versionNumber} of '{plan.Name}' is archived. Archiving is how a set of "
                + "numbers is taken out of circulation; putting it back means writing it as a new "
                + "version, so the reason it was withdrawn stays readable.");
        }

        var now = clock.GetUtcNow();
        plan.CurrentVersionNumber = versionNumber;

        var keys = PlanCatalogueService.ReadValues(version.ValuesJson).Keys.ToList();
        var announcements = await AnnounceAsync(plan, keys, now, cancellationToken);

        Record(plan, versionNumber, PlanChangeAction.VersionActivated, actor, reason, now,
            announcements.Count);

        catalogue.Invalidate();

        return (version, announcements);
    }

    /// <summary>Takes a version out of circulation.</summary>
    public async Task<(Plan Plan, PlanVersion Version)> ArchiveVersionAsync(
        string name, int versionNumber, string reason, string actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        RequireReason(reason);

        var plan = await RequirePlanAsync(name, cancellationToken);
        var version = await RequireVersionAsync(plan, versionNumber, cancellationToken);

        if (version.IsArchived)
        {
            throw new PlanRefusedException(PlanErrorCodes.AlreadyArchived,
                $"Version {versionNumber} of '{plan.Name}' was archived at {version.ArchivedAt:O}.");
        }

        if (plan.CurrentVersionNumber == versionNumber)
        {
            throw new PlanRefusedException(PlanErrorCodes.VersionInUse,
                $"Version {versionNumber} is what '{plan.Name}' currently sells. Activate another "
                + "version first, or archive the plan itself.");
        }

        var pinned = await db.PlanAssignments.CountAsync(
            assignment => assignment.PlanId == plan.Id && assignment.VersionNumber == versionNumber,
            cancellationToken);

        if (pinned > 0)
        {
            throw new PlanRefusedException(PlanErrorCodes.VersionInUse,
                $"{pinned} subject(s) are on version {versionNumber} of '{plan.Name}'. Move them to "
                + "another version first; archiving underneath them would take away what they are on.");
        }

        var now = clock.GetUtcNow();
        version.ArchivedAt = now;
        version.ArchivedBy = actor;
        version.ArchiveReason = reason.Trim();

        Record(plan, versionNumber, PlanChangeAction.VersionArchived, actor, reason, now, affected: 0);
        catalogue.Invalidate();

        return (plan, version);
    }

    /// <summary>Stops a plan being sold. Never a delete, and never while somebody is on it.</summary>
    public async Task<Plan> ArchivePlanAsync(
        string name, string reason, string actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        RequireReason(reason);

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Name == name, cancellationToken)
                   ?? throw new PlanRefusedException(PlanErrorCodes.UnknownPlan, UnknownPlanMessage(name));

        if (plan.IsArchived)
        {
            throw new PlanRefusedException(PlanErrorCodes.AlreadyArchived,
                $"'{plan.Name}' was archived at {plan.ArchivedAt:O} by {plan.ArchivedBy}. Archiving "
                + "it again would overwrite who did it and why.");
        }

        var radius = await BlastRadiusAsync(plan, cancellationToken);

        if (radius.TotalSubjects > 0)
        {
            throw new PlanRefusedException(PlanErrorCodes.PlanInUse,
                $"{radius.TotalSubjects} subject(s) are on '{plan.Name}'. Move them to another plan "
                + "first: archiving takes the numbers out of the catalogue, so anybody left on it "
                + "would silently lose what they are paying for.");
        }

        var now = clock.GetUtcNow();
        plan.Archive(actor, reason.Trim(), now);

        Record(plan, null, PlanChangeAction.PlanArchived, actor, reason, now, affected: 0);
        catalogue.Invalidate();

        return plan;
    }

    /// <summary>Puts a subject on a plan version, or moves them between versions.</summary>
    public async Task<(PlanAssignment Assignment, EntitlementsChanged Announcement)> AssignAsync(
        EntitlementSubject subject,
        string name,
        int? versionNumber,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        RequireReason(reason);

        if (string.IsNullOrWhiteSpace(subject.Id))
        {
            throw new PlanRefusedException(PlanErrorCodes.SubjectRequired,
                "An assignment needs a subject to attach to.");
        }

        var plan = await RequirePlanAsync(name, cancellationToken);
        var target = versionNumber ?? plan.CurrentVersionNumber;
        var version = await RequireVersionAsync(plan, target, cancellationToken);

        if (version.IsArchived)
        {
            throw new PlanRefusedException(PlanErrorCodes.VersionArchived,
                $"Version {target} of '{plan.Name}' is archived and is not something to put anybody on.");
        }

        var now = clock.GetUtcNow();
        var assignment = await catalogue.AssignmentForAsync(subject, cancellationToken);

        if (assignment is null)
        {
            assignment = new PlanAssignment
            {
                Id = PlanAssignment.GenerateId(),
                SubjectKind = subject.Kind,
                SubjectId = subject.Id,
                PlanId = plan.Id,
                VersionNumber = target,
                AssignedBy = actor,
                Reason = reason.Trim(),
                AssignedAt = now,
            };

            db.PlanAssignments.Add(assignment);
        }
        else
        {
            assignment.PlanId = plan.Id;
            assignment.VersionNumber = target;
            assignment.AssignedBy = actor;
            assignment.Reason = reason.Trim();
            assignment.AssignedAt = now;
        }

        var keys = PlanCatalogueService.ReadValues(version.ValuesJson).Keys.ToList();

        var announcement = new EntitlementsChanged
        {
            SubjectKind = subject.Kind,
            SubjectId = subject.Id,
            Reason = EntitlementsChangedReason.PlanAssignmentChanged,
            Version = await versions.AdvanceAsync(subject, cancellationToken),
            ChangedKeys = [.. keys],
            OccurredAt = now,
        };

        Record(plan, target, PlanChangeAction.SubjectAssigned, actor, reason, now, affected: 1,
            subject: $"{subject.Kind}:{subject.Id}");

        catalogue.Invalidate();

        return (assignment, announcement);
    }

    public async Task<PlanBlastRadius> BlastRadiusAsync(string name, CancellationToken cancellationToken) =>
        await BlastRadiusAsync(await RequirePlanAsync(name, cancellationToken), cancellationToken);

    /// <summary>
    /// How many subjects an edit to this plan would reach, broken down by the version they are on.
    /// </summary>
    public async Task<PlanBlastRadius> BlastRadiusAsync(Plan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var pinned = await db.PlanAssignments
            .Where(assignment => assignment.PlanId == plan.Id)
            .GroupBy(assignment => assignment.VersionNumber)
            .Select(group => new { Version = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var granted = await GrantedSubjectsAsync(plan, cancellationToken);

        var counts = pinned.ToDictionary(entry => entry.Version, entry => entry.Count);

        foreach (var (version, subjects) in granted)
        {
            counts[version] = counts.GetValueOrDefault(version) + subjects;
        }

        var isDefault = string.Equals(options.DefaultGuildPlan, plan.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(options.DefaultUserPlan, plan.Name, StringComparison.OrdinalIgnoreCase);

        return new PlanBlastRadius(
            plan.Name,
            plan.CurrentVersionNumber,
            counts.Values.Sum(),
            counts.OrderBy(entry => entry.Key)
                .Select(entry => new PlanVersionSubjects(entry.Key, entry.Value))
                .ToList(),
            isDefault);
    }

    /// <summary>Subjects reached through a live plan grant, by the version their grant resolves to,
    /// excluding anybody who is also pinned - the pin is the more specific answer and counting them
    /// twice would inflate the number an admin is about to make a decision on.</summary>
    private async Task<IReadOnlyDictionary<int, int>> GrantedSubjectsAsync(
        Plan plan, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var prefix = plan.Name + PlanReference.VersionSeparator;

        var rows = await db.Grants
            .Where(grant => grant.RevokedAt == null
                            && (grant.ExpiresAt == null || grant.ExpiresAt > now)
                            && grant.Plan != null
                            && (grant.Plan == plan.Name || grant.Plan.StartsWith(prefix)))
            .Select(grant => new { grant.Plan, grant.SubjectKind, grant.SubjectId })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return new Dictionary<int, int>();

        var pinnedSubjects = await db.PlanAssignments
            .Where(assignment => assignment.PlanId == plan.Id)
            .Select(assignment => new { assignment.SubjectKind, assignment.SubjectId })
            .ToListAsync(cancellationToken);

        var pinned = pinnedSubjects
            .Select(subject => $"{subject.SubjectKind}:{subject.SubjectId}")
            .ToHashSet(StringComparer.Ordinal);

        var counts = new Dictionary<int, int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var key = $"{row.SubjectKind}:{row.SubjectId}";
            if (pinned.Contains(key) || !seen.Add(key)) continue;

            var version = PlanReference.Split(row.Plan!).Version ?? plan.CurrentVersionNumber;

            counts[version] = counts.GetValueOrDefault(version) + 1;
        }

        return counts;
    }

    /// <summary>Every subject an activation has to announce: the pinned ones and the granted ones,
    /// deduplicated, capped at <see cref="AnnouncementLimit"/>.</summary>
    private async Task<IReadOnlyList<EntitlementSubject>> SubjectsOnPlanAsync(
        Plan plan, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var prefix = plan.Name + PlanReference.VersionSeparator;

        var assigned = await db.PlanAssignments
            .Where(assignment => assignment.PlanId == plan.Id)
            .OrderBy(assignment => assignment.Id)
            .Take(AnnouncementLimit)
            .Select(assignment => new { assignment.SubjectKind, assignment.SubjectId })
            .ToListAsync(cancellationToken);

        var granted = await db.Grants
            .Where(grant => grant.RevokedAt == null
                            && (grant.ExpiresAt == null || grant.ExpiresAt > now)
                            && grant.Plan != null
                            && (grant.Plan == plan.Name || grant.Plan.StartsWith(prefix)))
            .Select(grant => new { grant.SubjectKind, grant.SubjectId })
            .Distinct()
            .Take(AnnouncementLimit)
            .ToListAsync(cancellationToken);

        var subjects = new List<EntitlementSubject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in assigned.Concat(granted))
        {
            if (!seen.Add($"{row.SubjectKind}:{row.SubjectId}")) continue;
            if (subjects.Count == AnnouncementLimit) break;

            subjects.Add(new EntitlementSubject(row.SubjectKind, row.SubjectId));
        }

        return subjects;
    }

    private async Task<IReadOnlyList<EntitlementsChanged>> AnnounceAsync(
        Plan plan,
        IReadOnlyList<string> changedKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var subjects = await SubjectsOnPlanAsync(plan, cancellationToken);
        var announcements = new List<EntitlementsChanged>(subjects.Count);

        foreach (var subject in subjects)
        {
            announcements.Add(new EntitlementsChanged
            {
                SubjectKind = subject.Kind,
                SubjectId = subject.Id,
                Reason = EntitlementsChangedReason.PlanVersionActivated,

                // Advanced inside the same transaction as the version it describes, so a plan edit
                // that fails to commit does not leave every guild on the plan told to refetch
                // something that did not change.
                Version = await versions.AdvanceAsync(subject, cancellationToken),
                ChangedKeys = [.. changedKeys],
                OccurredAt = now,
            });
        }

        if (subjects.Count == AnnouncementLimit)
        {
            logger?.LogWarning(
                "Plan {Plan} reaches at least {Limit} subjects; the rest will pick the change up on "
                + "their cache TTL rather than being announced individually.", plan.Name, AnnouncementLimit);
        }

        return announcements;
    }

    /// <summary>
    /// Mirrors a newly written version into Stripe, on the publish path, before anything commits.
    /// </summary>
    private async Task MirrorToStripeAsync(
        Plan plan, PlanVersion version, CancellationToken cancellationToken)
    {
        if (stripe is null) return;

        try
        {
            var outcome = await stripe.SyncAsync(plan, version, cancellationToken);

            if (outcome is StripeSyncOutcome.NotConfigured)
            {
                logger?.LogDebug(
                    "Plan '{Plan}' version {Version} was not mirrored into Stripe: no secret key is "
                    + "configured on this instance.", plan.Name, version.VersionNumber);
            }
        }
        catch (StripeGatewayException failure)
        {
            throw new PlanRefusedException(PlanErrorCodes.StripeSyncFailed,
                $"'{plan.Name}' version {version.VersionNumber} was not published, because Stripe "
                + $"would not create the price for it: {failure.Message} Nothing was written, so "
                + "retrying is safe - the same request presents the same idempotency key and will "
                + "reuse whatever Stripe already made.", failure);
        }
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Checks a plan's values against the catalogue and against the one invariant an admin does not
    /// get to override.
    /// </summary>
    public static IReadOnlyDictionary<EntitlementKey, EntitlementValue> Validate(
        IReadOnlyDictionary<string, string> values)
    {
        if (values is not { Count: > 0 })
        {
            throw new PlanRefusedException(PlanErrorCodes.ValuesRequired,
                "A plan needs at least one entitlement value. A plan with none is a name that "
                + "resolves to the catalogue defaults, which is what having no plan already does.");
        }

        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();

        foreach (var (name, text) in values)
        {
            if (!EntitlementKeys.TryGet(name, out var key))
            {
                throw new PlanRefusedException(PlanErrorCodes.UnknownEntitlementKey,
                    $"'{name}' is not an entitlement key. Known keys: "
                    + $"{string.Join(", ", EntitlementKeys.All.Select(k => k.Name))}.");
            }

            try
            {
                parsed[key] = key.Parse(text ?? string.Empty);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new PlanRefusedException(PlanErrorCodes.InvalidEntitlementValue,
                    $"'{text}' is not a value '{name}' can take: {exception.Message}");
            }
        }

        RequirePlanIndependentCapabilities(parsed);

        return parsed;
    }

    /// <summary>The floor no plan may go under.</summary>
    private static void RequirePlanIndependentCapabilities(
        IReadOnlyDictionary<EntitlementKey, EntitlementValue> parsed)
    {
        if (!parsed.TryGetValue(EntitlementKeys.VoiceMaxParticipants, out var participants)) return;

        if (participants.AsNumber < MinimumVoiceParticipants)
        {
            throw new PlanRefusedException(PlanErrorCodes.WithholdsPlanIndependentModule,
                $"A plan cannot set voice.max_participants to {participants.AsNumber}. Voice channels, "
                + "moderation and AutoMod are outside what any plan may withhold, and a room that "
                + "admits fewer than two people withholds voice by arithmetic rather than by a flag. "
                + "What a plan buys is voice capacity - room size above that floor, the video ceiling, "
                + "concurrent publishers - never whether the module exists.");
        }
    }

    private static void ValidatePrice(long? priceMinorUnits, string? currency)
    {
        if (priceMinorUnits is null && string.IsNullOrWhiteSpace(currency)) return;

        if (priceMinorUnits is < 0)
        {
            throw new PlanRefusedException(PlanErrorCodes.InvalidPrice,
                "A price cannot be negative. Leave it null for a plan that is not sold.");
        }

        if (priceMinorUnits is > 0 && string.IsNullOrWhiteSpace(currency))
        {
            throw new PlanRefusedException(PlanErrorCodes.InvalidPrice,
                "A price needs a currency. An amount with no currency is a number nobody can charge.");
        }

        if (currency is not null && currency.Trim().Length is not (0 or 3))
        {
            throw new PlanRefusedException(PlanErrorCodes.InvalidPrice,
                $"'{currency}' is not an ISO 4217 currency code.");
        }
    }

    /// <summary>Trims, lowercases and checks the name.</summary>
    public static string NormaliseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PlanRefusedException(PlanErrorCodes.PlanNameRequired, "A plan needs a name.");
        }

        var trimmed = name.Trim().ToLowerInvariant();

        if (trimmed.Contains(PlanReference.VersionSeparator)
            || trimmed.Any(char.IsWhiteSpace)
            || trimmed.Length > 64)
        {
            throw new PlanRefusedException(PlanErrorCodes.InvalidPlanName,
                $"'{trimmed}' is not usable as a plan name: no whitespace, no "
                + $"'{PlanReference.VersionSeparator}' (that is how a pinned version is "
                + "addressed) and at most 64 characters.");
        }

        return trimmed;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static PlanVersion NewVersion(
        Plan plan,
        int number,
        IReadOnlyDictionary<EntitlementKey, EntitlementValue> values,
        long? priceMinorUnits,
        string? currency,
        string reason,
        string actor) => new()
    {
        Id = PlanVersion.GenerateId(),
        PlanId = plan.Id,
        VersionNumber = number,
        ValuesJson = JsonSerializer.Serialize(
            values.ToDictionary(entry => entry.Key.Name, entry => entry.Key.Format(entry.Value))),
        PriceMinorUnits = priceMinorUnits,
        Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToLowerInvariant(),
        Reason = reason.Trim(),
        CreatedBy = actor,
    };

    private void Record(
        Plan plan,
        int? versionNumber,
        PlanChangeAction action,
        string actor,
        string reason,
        DateTimeOffset at,
        int? affected,
        string? subject = null) =>
        db.PlanAuditEntries.Add(new PlanAuditEntry
        {
            Id = PlanAuditEntry.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = versionNumber,
            Action = action,
            Actor = actor,
            Reason = reason.Trim(),
            Subject = subject,
            AffectedSubjects = affected,
            OccurredAt = at,
        });

    private async Task<int> NextVersionNumberAsync(string planId, CancellationToken cancellationToken)
    {
        var highest = await db.PlanVersions
            .Where(version => version.PlanId == planId)
            .MaxAsync(version => (int?)version.VersionNumber, cancellationToken);

        return (highest ?? 0) + 1;
    }

    private async Task<Plan> RequirePlanAsync(string name, CancellationToken cancellationToken)
    {
        var normalised = NormaliseName(name);

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Name == normalised, cancellationToken)
                   ?? throw new PlanRefusedException(PlanErrorCodes.UnknownPlan,
                       UnknownPlanMessage(normalised));

        if (plan.IsArchived)
        {
            throw new PlanRefusedException(PlanErrorCodes.PlanArchived,
                $"'{plan.Name}' was archived at {plan.ArchivedAt:O}: {plan.ArchiveReason}");
        }

        return plan;
    }

    private async Task<PlanVersion> RequireVersionAsync(
        Plan plan, int versionNumber, CancellationToken cancellationToken) =>
        await db.PlanVersions.FirstOrDefaultAsync(
            version => version.PlanId == plan.Id && version.VersionNumber == versionNumber,
            cancellationToken)
        ?? throw new PlanRefusedException(PlanErrorCodes.UnknownVersion,
            $"'{plan.Name}' has no version {versionNumber}. Its current version is "
            + $"{plan.CurrentVersionNumber}.");

    private static string UnknownPlanMessage(string name) =>
        $"'{name}' is not a plan in this instance's catalogue. Plans seeded from configuration on "
        + "first start; if this is a fresh database the seed may not have run.";

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new PlanRefusedException(PlanErrorCodes.ReasonRequired,
                "A plan change needs a reason. This surface moves what every guild on a plan gets at "
                + "once, so 'who raised this ceiling and why' has to be answerable a year later.");
        }
    }

    private static void RequireActor(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new PlanRefusedException(PlanErrorCodes.ActorRequired,
                "A plan change records the staff account that made it, and this request carried none.");
        }
    }
}
