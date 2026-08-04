using Guild.Application.Dtos.Response;

namespace Guild.Application.Services;

/// <summary>
/// The one place a stored presence status becomes a status on the wire (privacy spec T0-5).
///
/// <para><b>The bug this exists to close.</b> <c>OnlineStatus.Hidden</c> is what a user picks to
/// appear offline. It was stored faithfully and then handed straight to peers - by
/// <c>GuildController</c>'s member projection and by the <c>guild.PresenceChanged</c> broadcast - so
/// anyone watching could tell "invisible" from "actually offline", which is the entire content of
/// the setting. The model was never wrong; only the projection was.</para>
///
/// <para><b>The rule.</b> <see cref="OnlineStatus.Hidden"/> renders as
/// <see cref="OnlineStatus.Offline"/> for every viewer except the user themselves. The user keeps
/// seeing their own real status, because a client that could not read it back could not render the
/// picker. <c>Hidden</c> must never reach a third party on the wire.</para>
///
/// <para>Every emit site routes through here rather than each remembering the rule. That is the
/// point: the two sites that leaked did so because each had its own copy of "take the status and
/// send it".</para>
/// </summary>
public static class PresenceProjection
{
    /// <summary>What an unknown or absent status resolves to. Absent means absent.</summary>
    public const OnlineStatus Fallback = OnlineStatus.Offline;

    /// <summary>
    /// Parses a stored status name. Presence is written as <see cref="OnlineStatus"/> member names,
    /// but the store is Redis and the writer is another service, so an unrecognised value is a case
    /// this has to have an answer for: it is treated as absent, the same direction the
    /// <c>@here</c> fan-out already falls in.
    /// </summary>
    public static bool TryParse(string? raw, out OnlineStatus status)
    {
        status = Fallback;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!Enum.TryParse(raw, ignoreCase: true, out OnlineStatus parsed)) return false;

        // Enum.TryParse also accepts "1", and Enum.IsDefined would happily confirm it - so a
        // caller that passed a raw ordinal would silently get Hidden. Requiring the input to be the
        // member's own name is what makes this a name parse rather than a cast.
        if (!string.Equals(Enum.GetName(parsed), raw.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        status = parsed;
        return true;
    }

    /// <summary>
    /// The status <paramref name="viewerIsSubject"/> may be shown.
    /// </summary>
    /// <param name="status">The real, stored status.</param>
    /// <param name="viewerIsSubject">True only when the recipient is the user the status belongs
    /// to. Anything else - another member, a bot, a federated peer - is a third party.</param>
    public static OnlineStatus ProjectFor(OnlineStatus status, bool viewerIsSubject) =>
        !viewerIsSubject && status == OnlineStatus.Hidden ? OnlineStatus.Offline : status;

    /// <summary>
    /// Name-in, name-out, for the realtime path - which carries the status as the string it was
    /// stored as. An unparseable value projects to <c>Offline</c> rather than being passed through,
    /// so a status this service does not understand cannot become a leak in a later release.
    /// </summary>
    public static string ProjectNameFor(string? rawStatus, bool viewerIsSubject)
    {
        if (!TryParse(rawStatus, out var status)) return nameof(OnlineStatus.Offline);

        return ProjectFor(status, viewerIsSubject).ToString();
    }
}
