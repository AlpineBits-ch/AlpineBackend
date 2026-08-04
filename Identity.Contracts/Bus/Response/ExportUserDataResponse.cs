namespace Identity.Contracts.Bus.Response;

/// <summary>
/// One service's answer to <c>ExportUserDataCommand</c> - the read-side sibling of <see
/// cref="PurgeUserDataCommandResponse"/>.
/// </summary>
public class ExportUserDataResponse
{
    public string ExportId { get; set; }

    public string UserId { get; set; }

    /// <summary>Fixed lowercase service identifier - must match the saga's participant list.</summary>
    public string Service { get; set; }

    /// <summary>
    /// The whole fragment as a JSON document, serialized by the producing service.
    /// </summary>
    public string FragmentJson { get; set; } = "{}";

    /// <summary>
    /// Row counts per collection in the fragment, copied into the archive's <c>manifest.json</c>.
    /// </summary>
    public Dictionary<string, int> RowCounts { get; set; } = new();

    /// <summary>Set when the service could not produce its section.</summary>
    public string? Error { get; set; }
}
