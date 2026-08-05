namespace Identity.Application.Dtos.Request;

/// <summary>The caller's complete list of suppressed games.</summary>
public class SetHiddenActivitiesDto
{
    public IReadOnlyList<HiddenActivityDto>? Entries { get; set; }
}

public class HiddenActivityDto
{
    /// <summary>The application id to suppress. Preferred whenever the client has one.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>The activity name to suppress, for sources that produce no application id.</summary>
    public string? Name { get; set; }
}

/// <summary>Limits for the suppression list, in one place because the endpoint and its tests both
/// need them.</summary>
public static class HiddenActivityLimits
{
    /// <summary>The set rides inside every cached copy of the account's privacy record, which is
    /// read on every presence broadcast - so it is bounded rather than left to grow.</summary>
    public const int MaxEntries = 200;

    public const int MaxApplicationIdLength = 20;
    public const int MaxNameLength = 128;
}
