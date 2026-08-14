using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;

namespace Echo.Dtos.Entitlements;

/// <summary>What this instance is, from the client's point of view.</summary>
public sealed record EntitlementInstanceInfo(string LicenseMode, bool UpgradesAvailable);

/// <summary>How long a client may cache an entitlement snapshot.</summary>
public sealed class EntitlementReadOptions
{
    /// <summary>Deliberately shorter than any plausible resolver cache backstop.</summary>
    public int TtlSeconds { get; set; } = 60;
}

/// <summary>Assembles the client-facing entitlement snapshot.</summary>
public sealed class EntitlementSnapshotBuilder(
    EntitlementResolver resolver,
    IEntitlementVersionProvider versions,
    EntitlementInstanceInfo instance,
    EntitlementReadOptions options,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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
            remedy);
    }
}
