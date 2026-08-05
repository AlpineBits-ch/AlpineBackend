using Guild.Application.Dtos.Response;
using Identity.Contracts.Bus.Response;
using Social.Contracts.Dtos;

namespace Guild.Application.Services;

/// <summary>
/// The one place a stored presence status becomes a status on the wire (privacy spec T0-5).
/// </summary>
public static class PresenceProjection
{
    /// <summary>What an unknown or absent status resolves to. Absent means absent.</summary>
    public const OnlineStatus Fallback = OnlineStatus.Offline;

    /// <summary>Parses a stored status name.</summary>
    public static bool TryParse(string? raw, out OnlineStatus status)
    {
        status = Fallback;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!Enum.TryParse(raw, ignoreCase: true, out OnlineStatus parsed)) return false;

        // Enum.TryParse also accepts "1", and Enum.IsDefined would happily confirm it - so a caller
        // that passed a raw ordinal would silently get Hidden.
        if (!string.Equals(Enum.GetName(parsed), raw.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        status = parsed;
        return true;
    }

    /// <summary>The status <paramref name="viewerIsSubject"/> may be shown.</summary>
    /// <param name="status">The real, stored status.</param>
    /// <param name="viewerIsSubject">
    /// True only when the recipient is the user the status belongs to.
    /// </param>
    public static OnlineStatus ProjectFor(OnlineStatus status, bool viewerIsSubject) =>
        !viewerIsSubject && status == OnlineStatus.Hidden ? OnlineStatus.Offline : status;

    /// <summary>
    /// Name-in, name-out, for the realtime path - which carries the status as the string it was
    /// stored as.
    /// </summary>
    public static string ProjectNameFor(string? rawStatus, bool viewerIsSubject)
    {
        if (!TryParse(rawStatus, out var status)) return nameof(OnlineStatus.Offline);

        return ProjectFor(status, viewerIsSubject).ToString();
    }

    /// <summary>The activities <paramref name="viewerIsSubject"/> may be shown.</summary>
    /// <param name="activities">The real, stored activities.</param>
    /// <param name="status">The real, stored status - not the already-projected one.</param>
    /// <param name="viewerIsSubject">True only when the recipient is the user themselves.</param>
    /// <param name="shareActivity">
    /// <c>UserPrivacySettings.ShareActivity</c> for the subject.
    /// </param>
    /// <param name="hidden">The subject's per-application suppression set.</param>
    public static IReadOnlyList<ActivityDto> ProjectActivitiesFor(
        IReadOnlyList<ActivityDto>? activities,
        OnlineStatus status,
        bool viewerIsSubject,
        bool shareActivity,
        HiddenActivitySummary? hidden = null)
    {
        if (activities is null || activities.Count == 0) return [];

        if (viewerIsSubject) return activities;

        // This gate must stay above the suppression set below.
        if (!shareActivity) return [];

        if (status == OnlineStatus.Hidden) return [];

        if (hidden is null) return activities;

        var visible = activities.Where(a => !hidden.Suppresses(a.ApplicationId, a.Name)).ToList();

        return visible.Count == activities.Count ? activities : visible;
    }

    /// <summary>
    /// Name-in overload for the realtime path, which carries the status as the string it was stored
    /// as.
    /// </summary>
    public static IReadOnlyList<ActivityDto> ProjectActivitiesFor(
        IReadOnlyList<ActivityDto>? activities,
        string? rawStatus,
        bool viewerIsSubject,
        bool shareActivity,
        HiddenActivitySummary? hidden = null)
    {
        TryParse(rawStatus, out var status);

        return ProjectActivitiesFor(activities, status, viewerIsSubject, shareActivity, hidden);
    }
}
