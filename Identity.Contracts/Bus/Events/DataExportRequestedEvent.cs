namespace Identity.Contracts.Bus.Events;

/// <summary>Published by Identity's <c>DataExportController</c> once a <c>DataExportRequest</c> row
/// is committed. Echo's <c>ExportUserDataSaga</c> starts on this and fans <c>ExportUserDataCommand</c>
/// out to every participating service - the same relationship <see cref="AccountPurgeStartedEvent"/>
/// has with the deletion saga.</summary>
public class DataExportRequestedEvent
{
    public string ExportId { get; set; }

    public string UserId { get; set; }
}
