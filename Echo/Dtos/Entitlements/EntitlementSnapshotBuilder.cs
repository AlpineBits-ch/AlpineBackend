using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;

namespace Echo.Dtos.Entitlements;

/// <summary>What this instance is, from the client's point of view.</summary>
public sealed record EntitlementInstanceInfo(
    string LicenseMode,
    bool UpgradesAvailable,
    string? StripePublishableKey = null);

/// <summary>How long a client may cache an entitlement snapshot.</summary>
public sealed class EntitlementReadOptions
{
    /// <summary>Deliberately shorter than any plausible resolver cache backstop.</summary>
    public int TtlSeconds { get; set; } = 60;
}

/// <summary>Assembles the client-facing entitlement snapshot.</summary>
/// <param name="planFallback">
/// The configured default, used only when the registered assignment could not be asked.
/// </param>
public sealed class EntitlementSnapshotBuilder(
    EntitlementResolver resolver,
    IEntitlementVersionProvider versions,
    EntitlementInstanceInfo instance,
    EntitlementReadOptions options,
    TimeProvider? clock = null,
    IPlanAssignment? planAssignment = null,
    IPlanCatalogueSource? plans = null,
    FixedPlanAssignment? planFallback = null,
    ILogger<EntitlementSnapshotBuilder>? logger = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Whether a plan is a thing this instance has at all.</summary>
    private bool PlansApply =>
        planAssignment is not null
        && plans is not null
        && !string.Equals(instance.LicenseMode, LicenseModes.SelfHostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The caller's own entitlements.</summary>
    public Task<EntitlementSnapshotDto> ForUserAsync(string userId, CancellationToken cancellationToken = default) =>
        BuildAsync(
            EntitlementSubject.ForUser(userId),
            EntitlementRemedyPolicy.For(
                EntitlementDegradationReason.UserPlanLimit, EntitlementBoundBy.User,
                instance.UpgradesAvailable, actorCanManageGuild: false),
            cancellationToken);

    /// <summary>One guild's own entitlements.</summary>
    public Task<EntitlementSnapshotDto> ForGuildAsync(
        string guildId, bool actorCanManageGuild, CancellationToken cancellationToken = default) =>
        BuildAsync(
            EntitlementSubject.ForGuild(guildId),
            EntitlementRemedyPolicy.For(
                EntitlementDegradationReason.GuildPlanLimit, EntitlementBoundBy.Guild,
                instance.UpgradesAvailable, actorCanManageGuild),
            cancellationToken);

    private async Task<EntitlementSnapshotDto> BuildAsync(
        EntitlementSubject subject, EntitlementRemedyDecision remedy, CancellationToken cancellationToken)
    {
        var set = await resolver.ResolveAsync(subject, cancellationToken);
        var version = await versions.VersionAsync(subject, cancellationToken);

        return EntitlementSnapshotDto.From(
            set,
            subject,
            instance.LicenseMode,
            instance.UpgradesAvailable,
            version,
            options.TtlSeconds,
            _clock.GetUtcNow(),
            remedy,
            catalogue: null,
            plan: await PlanAsync(subject, cancellationToken),
            stripePublishableKey: instance.StripePublishableKey);
    }

    /// <summary>The subject's plan, resolved through the same assignment and the same catalogue the
    /// plan source resolves its values through, so the name on the snapshot and the numbers under it
    /// cannot disagree.</summary>
    private async Task<EntitlementPlanDto?> PlanAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        if (!PlansApply) return null;

        var catalogue = await plans!.CurrentAsync(cancellationToken);
        var reference = await ReferenceAsync(subject, cancellationToken);

        return EntitlementPlanDto.Resolve(reference, catalogue);
    }

    /// <summary>Which plan this subject is on, falling back to the configured default when the
    /// registered assignment could not be asked at all. See <c>planFallback</c>: the numbers on this
    /// snapshot came from the resolver's own fail-open path during an outage, and the plan name has to
    /// degrade the same way rather than turn a readable screen into an error.</summary>
    private async ValueTask<string?> ReferenceAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        try
        {
            return await planAssignment!.PlanNameForAsync(subject, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception,
                "Could not read the plan assignment for {Subject}. Naming this instance's configured "
                + "default instead; the ceilings on this snapshot are the resolver's own answer and are "
                + "unaffected.", subject);

            return planFallback?.DefaultFor(subject.Kind);
        }
    }
}
