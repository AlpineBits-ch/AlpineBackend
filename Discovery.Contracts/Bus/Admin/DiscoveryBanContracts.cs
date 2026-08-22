namespace Discovery.Contracts.Bus.Admin;

/// <summary>Bans a guild out of the discovery directory. StaffUserId must come from the resolved
/// acting principal, never from client input - the gateway controller owns that.</summary>
public class BanGuildFromDiscoveryRequest
{
    public string GuildId { get; set; } = null!;

    /// <summary>Owner-facing. Returned in a publish refusal.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>Staff-facing only. Never returned to a guild member.</summary>
    public string? StaffNote { get; set; }

    public string StaffUserId { get; set; } = null!;

    /// <summary>Null means indefinite.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class BanGuildFromDiscoveryResponse
{
    public string BanId { get; set; } = null!;
}

/// <summary>Lifts the guild's active ban(s). Does not republish a suspended listing - returning a
/// community to the public feed stays the owner's decision.</summary>
public class LiftDiscoveryBanRequest
{
    public string GuildId { get; set; } = null!;
    public string StaffUserId { get; set; } = null!;
}

public class LiftDiscoveryBanResponse
{
    public bool Lifted { get; set; }
}

/// <summary>Active bans only by default; every ban, lifted ones included, when IncludeLifted is
/// set.</summary>
public class ListDiscoveryBansRequest
{
    public bool IncludeLifted { get; set; }
}

public class ListDiscoveryBansResponse
{
    public List<DiscoveryBanSummary> Bans { get; set; } = [];
}

/// <summary>Staff-facing, so StaffNote is present here - unlike anything a guild member can reach.</summary>
public class DiscoveryBanSummary
{
    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string? StaffNote { get; set; }
    public string BannedByUserId { get; set; } = null!;
    public DateTimeOffset BannedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }
    public string? LiftedByUserId { get; set; }
}

/// <summary>The console's own search over published listings, to find the guild before banning it.</summary>
public class SearchDiscoveryListingsRequest
{
    public string? Query { get; set; }
    public string? Cursor { get; set; }
}

public class SearchDiscoveryListingsResponse
{
    public List<DiscoveryListingSummary> Listings { get; set; } = [];
    public string? NextCursor { get; set; }
}

public class DiscoveryListingSummary
{
    public string GuildId { get; set; } = null!;
    public string GuildName { get; set; } = null!;
    public string Headline { get; set; } = null!;
    public string State { get; set; } = null!;
    public DateTimeOffset? PublishedAt { get; set; }
}
