namespace Identity.Contracts.Bus.Response;

/// <summary>
/// One service's answer to <c>ExportUserDataCommand</c> - the read-side sibling of
/// <see cref="PurgeUserDataCommandResponse"/>. <see cref="Service"/> is the same fixed lowercase
/// identifier that response uses ("identity", "social", "guild", "messaging", "federation",
/// "import", "bots", "isle"), and <c>ExportUserDataSaga</c> tracks acknowledgement by it, so the
/// string here must match the entry in that list exactly.
///
/// <para><b>The fragment must contain no personal data about anyone but the subject.</b> Messages
/// the subject sent, yes; another member's email address, phone number or IP, never. Other people
/// appear as opaque ids only. Every participant's handler carries that rule in its own doc comment
/// and its own test, because the archive is assembled blind - the assembler cannot inspect a
/// fragment for somebody else's address, and by the time it could the leak has already happened.</para>
/// </summary>
public class ExportUserDataResponse
{
    public string ExportId { get; set; }

    public string UserId { get; set; }

    /// <summary>Fixed lowercase service identifier - must match the saga's participant list.</summary>
    public string Service { get; set; }

    /// <summary>
    /// The whole fragment as a JSON document, serialized by the producing service.
    ///
    /// <para>A string rather than a typed graph on purpose: each service owns the shape of its own
    /// section of the archive, and a shared DTO would make every schema change a contract change
    /// coordinated across eight services. The assembler writes it into the zip verbatim.</para>
    /// </summary>
    public string FragmentJson { get; set; } = "{}";

    /// <summary>Row counts per collection in the fragment, copied into the archive's
    /// <c>manifest.json</c>. What makes "is this export complete" answerable without parsing every
    /// fragment - and what makes an empty section distinguishable from a section a service failed to
    /// produce.</summary>
    public Dictionary<string, int> RowCounts { get; set; } = new();

    /// <summary>Set when the service could not produce its section. The archive is still assembled
    /// and still delivered - a subject waiting on an access request is better served by seven
    /// sections and an honest note than by nothing - but the manifest records the failure.</summary>
    public string? Error { get; set; }
}
