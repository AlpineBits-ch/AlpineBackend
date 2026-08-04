namespace Identity.Contracts.Bus.Response;

/// <summary>The link types this instance knows how to report. Stable wire values - a client keys its
/// icon and its deep link off them.</summary>
public static class ExternalConnectionTypes
{
    public const string Steam = "steam";
}

public class GetUserConnectionsResponse
{
    public ICollection<UserConnectionsSummary> Users { get; set; } = new List<UserConnectionsSummary>();
}

/// <summary>
/// One account's linked external accounts. An empty <see cref="Connections"/> covers "nothing linked",
/// "purged", "unknown id" and "the account's ConnectionsVisibility is Nobody" alike - see
/// <see cref="UserBirthdaySummary"/> for why those are not distinguished.
/// </summary>
public class UserConnectionsSummary
{
    public string UserId { get; set; } = null!;
    public ICollection<ExternalConnectionSummary> Connections { get; set; } = new List<ExternalConnectionSummary>();
}

/// <summary>
/// A single linked external account.
///
/// <para>Shaped as <c>{ type, externalId, displayName? }</c> rather than a provider-specific field so
/// adding a second provider is additive. <see cref="ExternalId"/> is the provider's own identifier -
/// for Steam, the SteamID64 - and is the correlation handle the <c>ConnectionsVisibility</c> gate
/// exists to control.</para>
/// </summary>
public class ExternalConnectionSummary
{
    /// <summary>One of <see cref="ExternalConnectionTypes"/>.</summary>
    public string Type { get; set; } = null!;

    public string ExternalId { get; set; } = null!;

    /// <summary>The provider-side display name, when this instance has one. Null for Steam today -
    /// nothing in this codebase ever fetches a Steam persona name, and inventing a second outbound
    /// call to Valve on every profile read to fill it in is not worth the field.</summary>
    public string? DisplayName { get; set; }
}
