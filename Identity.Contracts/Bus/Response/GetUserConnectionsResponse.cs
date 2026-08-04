namespace Identity.Contracts.Bus.Response;

/// <summary>The link types this instance knows how to report.</summary>
public static class ExternalConnectionTypes
{
    public const string Steam = "steam";
}

public class GetUserConnectionsResponse
{
    public ICollection<UserConnectionsSummary> Users { get; set; } = new List<UserConnectionsSummary>();
}

/// <summary>One account's linked external accounts.</summary>
public class UserConnectionsSummary
{
    public string UserId { get; set; } = null!;
    public ICollection<ExternalConnectionSummary> Connections { get; set; } = new List<ExternalConnectionSummary>();
}

/// <summary>A single linked external account.</summary>
public class ExternalConnectionSummary
{
    /// <summary>One of <see cref="ExternalConnectionTypes"/>.</summary>
    public string Type { get; set; } = null!;

    public string ExternalId { get; set; } = null!;

    /// <summary>The provider-side display name, when this instance has one.</summary>
    public string? DisplayName { get; set; }
}
