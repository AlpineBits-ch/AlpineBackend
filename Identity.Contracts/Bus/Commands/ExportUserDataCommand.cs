namespace Identity.Contracts.Bus.Commands;

/// <summary>
/// Fanned out (broadcast, not point-to-point) by Echo's <c>ExportUserDataSaga</c> to every
/// participating service - the read-side sibling of <see cref="PurgeUserDataCommand"/>. Each service
/// has its own handler bound to its own queue via Wolverine's conventional routing, so a single
/// publish reaches all of them, and each answers with one
/// <c>Identity.Contracts.Bus.Response.ExportUserDataResponse</c>.
///
/// <para>Handlers must be idempotent and, unlike the purge, must be <b>read-only</b>: a redelivery
/// re-reads and re-answers, and the saga discards the duplicate. Nothing an export handler does may
/// change the subject's data - an access request is not a write.</para>
/// </summary>
public class ExportUserDataCommand
{
    /// <summary>The <c>DataExportRequest</c> row this fan-out belongs to. Also the saga id, so a
    /// second export for the same account never collides with an in-flight one.</summary>
    public string ExportId { get; set; }

    public string UserId { get; set; }
}
