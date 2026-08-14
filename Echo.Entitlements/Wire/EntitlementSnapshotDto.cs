using System.Text.Json.Serialization;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Wire;

/// <summary>
/// One subject's effective entitlements, as a client reads them before it draws anything.
/// </summary>
/// <param name="LicenseMode"><c>selfhost</c> or <c>hosted</c>.</param>
/// <param name="UpgradesAvailable">Whether anything can actually be bought here.</param>
/// <param name="Version">Monotonic per subject.</param>
/// <param name="TtlSeconds">How long this may be cached.</param>
/// <param name="Ladders">
/// Every ladder referenced by the keys above, lowest rung first, with the metrics each rung stands
/// for.
/// </param>
public sealed record EntitlementSnapshotDto(
    string LicenseMode,
    bool UpgradesAvailable,
    int VocabularyVersion,
    EntitlementSubjectDto Subject,
    DateTimeOffset ResolvedAt,
    long Version,
    int TtlSeconds,
    IReadOnlyDictionary<string, EntitlementValueDto> Entitlements,
    IReadOnlyDictionary<string, IReadOnlyList<EntitlementRungDto>> Ladders,
    string Remedy,
    bool ActorCanRemedy)
{
    /// <summary>The snapshot for a resolved set.</summary>
    /// <param name="set">Resolved for this subject.</param>
    /// <param name="remedy">
    /// What an upgrade would be here and whether this caller could buy it.
    /// </param>
    public static EntitlementSnapshotDto From(
        EntitlementSet set,
        EntitlementSubject subject,
        string licenseMode,
        bool upgradesAvailable,
        long version,
        int ttlSeconds,
        DateTimeOffset resolvedAt,
        EntitlementRemedyDecision remedy,
        IReadOnlyList<EntitlementKey>? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(set);

        var keys = (catalogue ?? EntitlementKeys.All).Where(key => key.AppliesTo(subject.Kind)).ToList();

        var values = new Dictionary<string, EntitlementValueDto>(StringComparer.Ordinal);
        var ladders = new Dictionary<string, IReadOnlyList<EntitlementRungDto>>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            values[key.Name] = EntitlementValueDto.From(key, set.Value(key));

            if (key.Ladder is not null && !ladders.ContainsKey(key.Ladder.Name))
            {
                ladders[key.Ladder.Name] = EntitlementRungDto.Describe(key.Ladder);
            }
        }

        return new EntitlementSnapshotDto(
            licenseMode,
            upgradesAvailable,
            EntitlementContract.VocabularyVersion,
            EntitlementSubjectDto.From(subject),
            resolvedAt,
            version,
            ttlSeconds,
            values,
            ladders,
            remedy.Remedy,
            remedy.ActorCanRemedy);
    }
}

/// <summary>One rung, with what it actually permits.</summary>
/// <param name="MaxHeight">Tallest frame this rung permits, in pixels.</param>
/// <param name="MaxFramerate">Highest framerate this rung permits.</param>
public sealed record EntitlementRungDto(
    string Rung,
    int Rank,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxHeight,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxFramerate)
{
    public static IReadOnlyList<EntitlementRungDto> Describe(EntitlementLadder ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);

        var rungs = new List<EntitlementRungDto>(ladder.Rungs.Count);

        for (var rank = 0; rank < ladder.Rungs.Count; rank++)
        {
            var name = ladder.Rungs[rank];
            rungs.Add(VideoRungs.TryMetrics(name, out var height, out var framerate)
                ? new EntitlementRungDto(name, rank, height, framerate)
                : new EntitlementRungDto(name, rank, null, null));
        }

        return rungs;
    }
}

/// <summary>The (resolution, framerate) to rung mapping, which is server policy.</summary>
public static class VideoRungs
{
    /// <summary>What a rung permits, parsed from its name.</summary>
    public static bool TryMetrics(string rung, out int maxHeight, out int maxFramerate)
    {
        maxHeight = 0;
        maxFramerate = 0;

        if (string.IsNullOrWhiteSpace(rung)) return false;

        if (string.Equals(rung, "none", StringComparison.OrdinalIgnoreCase)) return true;

        var split = rung.IndexOf('p');
        if (split <= 0 || split == rung.Length - 1) return false;

        return int.TryParse(rung.AsSpan(0, split), out maxHeight)
               && int.TryParse(rung.AsSpan(split + 1), out maxFramerate);
    }

    /// <summary>What a publisher asking for this size may actually send on this rung.</summary>
    public static (int Height, int Framerate) Clamp(string rung, int height, int framerate)
    {
        if (!TryMetrics(rung, out var maxHeight, out var maxFramerate))
        {
            throw new ArgumentException($"'{rung}' is not a video rung.", nameof(rung));
        }

        return (Math.Min(height, maxHeight), Math.Min(framerate, maxFramerate));
    }

    /// <summary>
    /// The lowest rung that covers this request, or the highest rung when nothing does.
    /// </summary>
    public static string RungFor(EntitlementLadder ladder, int height, int framerate)
    {
        ArgumentNullException.ThrowIfNull(ladder);

        foreach (var rung in ladder.Rungs)
        {
            if (TryMetrics(rung, out var maxHeight, out var maxFramerate)
                && maxHeight >= height && maxFramerate >= framerate)
            {
                return rung;
            }
        }

        return ladder.Rungs[ladder.HighestRank];
    }
}

/// <summary>What a subject has used of the countable keys.</summary>
public sealed record EntitlementUsageDto(
    EntitlementSubjectDto Subject,
    DateTimeOffset ResolvedAt,
    IReadOnlyDictionary<string, long> Used);

/// <summary>The realtime envelope, and only an envelope.</summary>
public sealed record EntitlementsChangedDto(
    string SubjectKind,
    string SubjectId,
    long Version,
    IReadOnlyList<string> ChangedKeys)
{
    public static EntitlementsChangedDto For(
        EntitlementSubject subject, long version, IReadOnlyList<string>? changedKeys = null) =>
        new(EntitlementSubjectKinds.Of(subject.Kind), subject.Id, version, changedKeys ?? []);
}

/// <summary>The realtime event names.</summary>
public static class EntitlementRealtimeEvents
{
    /// <summary>Something in this subject's entitlements changed. Refetch what you have open.</summary>
    public const string Changed = "entitlements.Changed";
}

/// <summary>The limits in force in one voice room, for the voice snapshot.</summary>
/// <param name="PublisherCount">
/// Room state rather than entitlement state, and here because a cap without a count is
/// unrenderable: "2 of 2 people are sharing" is a sentence, "you cannot share" is a mystery.
/// </param>
public sealed record VoiceRoomLimitsDto(
    EntitlementValueDto MaxParticipants,
    EntitlementValueDto VideoCeiling,
    EntitlementValueDto MaxPublishers,
    int PublisherCount);

/// <summary>Where the snapshot's <c>version</c> comes from.</summary>
public interface IEntitlementVersionProvider
{
    ValueTask<long> VersionAsync(EntitlementSubject subject, CancellationToken cancellationToken = default);
}

/// <summary>The shipped implementation until Billing owns a counter.</summary>
public sealed class StaticEntitlementVersionProvider : IEntitlementVersionProvider
{
    public ValueTask<long> VersionAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0L);
}
