using Identity.Domain.Entities;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>
/// An export request as the subject sees it (T1-7).
///
/// <para>The property is <c>ExportId</c>, not <c>Id</c>, because that is the name the spec's response
/// shape uses and the name a client will key its polling on.</para>
///
/// <para><b>No artifact key.</b> The key is a bearer credential for the archive; the only thing that
/// ever hands it out is the download route, and then only as a signed URL that expires in
/// minutes.</para>
/// </summary>
public class DataExportRequestDto
{
    public string ExportId { get; set; } = null!;

    public DataExportStatus Status { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Null until the archive is ready. Once set, this is the moment after which the
    /// download route answers <c>410</c> - stated up front so a client can tell the subject how long
    /// they have rather than discovering it on a failed download.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when <see cref="Status"/> is <c>Failed</c> or <c>Partial</c>. Deliberately a
    /// plain sentence: the subject is entitled to know their request failed or came back short, and
    /// to nothing about why it did so internally.
    ///
    /// <para>Carrying the partial explanation on this existing field rather than a new one is what
    /// lets a client written before <c>Partial</c> existed still say something true - it already
    /// renders this string, and the string names the missing services.</para></summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// The services whose section is absent from the archive. Empty on every status but
    /// <c>Partial</c>, and never null, so a client can bind to it unconditionally.
    ///
    /// <para>The machine-readable half of the same fact <see cref="FailureReason"/> states in prose:
    /// a client that wants to render "messaging, isle" as chips, or to decide whether the gap
    /// matters for what the subject asked for, needs the names and not a sentence.</para>
    /// </summary>
    public List<string> MissingServices { get; set; } = [];

    public static DataExportRequestDto From(DataExportRequest request) => new()
    {
        ExportId = request.Id,
        Status = request.Status,
        RequestedAt = request.RequestedAt,
        CompletedAt = request.CompletedAt,
        ExpiresAt = request.ExpiresAt,
        FailureReason = request.FailureReason,
        MissingServices = [..request.MissingServices],
    };
}
