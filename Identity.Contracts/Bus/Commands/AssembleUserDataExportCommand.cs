namespace Identity.Contracts.Bus.Commands;

/// <summary>One participant's section of an archive, as carried from the saga to the assembler.
/// Structurally the payload half of <c>ExportUserDataResponse</c>, restated here so the assemble
/// command does not depend on the response type it was collected from.</summary>
public class UserDataExportFragment
{
    public string Service { get; set; }

    public string FragmentJson { get; set; } = "{}";

    public Dictionary<string, int> RowCounts { get; set; } = new();

    public string? Error { get; set; }
}

/// <summary>
/// Sent by Echo's <c>ExportUserDataSaga</c> to Identity once every participant has answered. Identity
/// owns the <c>DataExportRequest</c> row and the artifact bucket, so it - not the gateway - is what
/// writes the zip, uploads it and flips the row to <c>Ready</c>.
///
/// <para>The deletion saga has no equivalent step because a purge has nothing to hand back: each
/// participant's acknowledgement <i>is</i> the result. An export's participants produce data, and
/// that data has to end up somewhere with a lifetime and an owner.</para>
/// </summary>
public class AssembleUserDataExportCommand
{
    public string ExportId { get; set; }

    public string UserId { get; set; }

    public List<UserDataExportFragment> Fragments { get; set; } = new();
}
