namespace Identity.Contracts.Bus.Commands;

/// <summary>
/// Fanned out (broadcast, not point-to-point) by Echo's <c>ExportUserDataSaga</c> to every
/// participating service - the read-side sibling of <see cref="PurgeUserDataCommand"/>.
/// </summary>
public class ExportUserDataCommand
{
    /// <summary>The <c>DataExportRequest</c> row this fan-out belongs to.</summary>
    public string ExportId { get; set; }

    public string UserId { get; set; }
}
