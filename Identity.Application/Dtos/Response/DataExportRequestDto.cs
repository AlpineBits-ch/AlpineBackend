using Identity.Domain.Entities;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>An export request as the subject sees it (T1-7).</summary>
public class DataExportRequestDto
{
    public string ExportId { get; set; } = null!;

    public DataExportStatus Status { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Null until the archive is ready.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when <see cref="Status"/> is <c>Failed</c> or <c>Partial</c>.</summary>
    public string? FailureReason { get; set; }

    /// <summary>The services whose section is absent from the archive.</summary>
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
