using Guild.Application.Dtos.Response;

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
}
