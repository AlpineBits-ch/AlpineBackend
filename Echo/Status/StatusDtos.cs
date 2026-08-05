using System.Text;
using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;

namespace Echo.Status;

/// <summary>The public payloads.</summary>
public sealed record StatusSummaryDto(
    string Indicator,
    DateTimeOffset UpdatedAt,
    StatusBannerDto? Banner,
    IReadOnlyList<StatusComponentDto> Components,
    IReadOnlyList<StatusIncidentDto> Incidents,
    IReadOnlyList<StatusIncidentDto> Maintenance,
    IReadOnlyList<StatusIncidentDto> Recent);

/// <summary>The one thing a client renders, pre-composed.</summary>
public sealed record StatusBannerDto(
    string Title,
    string Body,
    string Severity,
    string IncidentReference,
    string Url,
    string? Template,
    string? ComponentKey);

public sealed record StatusComponentDto(
    string Key,
    string Name,
    string? Description,
    string Status,
    DateTimeOffset StatusSince,
    double? Uptime90d);

public sealed record StatusIncidentDto(
    string Reference,
    string Kind,
    string Title,
    string Impact,
    string Status,
    IReadOnlyList<string> Components,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? ScheduledUntil,
    string? Template,
    string Url,
    IReadOnlyList<StatusUpdateDto> Updates);

public sealed record StatusUpdateDto(
    string Status,
    string Body,
    string? Template,
    DateTimeOffset PostedAt);

/// <summary>The 90-day strip.</summary>
public sealed record StatusUptimeDto(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StatusUptimeComponentDto> Components);

public sealed record StatusUptimeComponentDto(
    string Key,
    string Name,
    double? Uptime90d,
    IReadOnlyList<StatusUptimeDayDto> Days);

/// <summary><c>Uptime</c> is null for a day with no record at all - before the component existed, or
/// while the gateway was itself down. The page draws those grey rather than green, because "we have
/// no idea" and "it was fine" are different answers.</summary>
public sealed record StatusUptimeDayDto(DateOnly Day, double? Uptime, string Status);

public static class StatusText
{
    /// <summary>PascalCase to snake_case. <c>PartialOutage</c> becomes <c>partial_outage</c>.</summary>
    public static string Slug(Enum value)
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0) builder.Append('_');
                builder.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                builder.Append(name[i]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The worst component decides the page's one-word verdict, except that maintenance only wins
    /// when nothing else is wrong.
    /// </summary>
    public static StatusIndicator Indicator(IEnumerable<ComponentStatus> statuses)
    {
        var worst = StatusIndicator.Operational;
        var maintenance = false;

        foreach (var status in statuses)
        {
            var candidate = status switch
            {
                ComponentStatus.MajorOutage => StatusIndicator.MajorOutage,
                ComponentStatus.PartialOutage => StatusIndicator.PartialOutage,
                ComponentStatus.DegradedPerformance => StatusIndicator.Degraded,
                ComponentStatus.UnderMaintenance => StatusIndicator.Operational,
                _ => StatusIndicator.Operational,
            };

            if (status == ComponentStatus.UnderMaintenance) maintenance = true;
            if (candidate > worst) worst = candidate;
        }

        return worst == StatusIndicator.Operational && maintenance
            ? StatusIndicator.Maintenance
            : worst;
    }

    /// <summary>How loudly a client should render the banner.</summary>
    public static string Severity(IncidentImpact impact, IncidentKind kind) => kind switch
    {
        IncidentKind.Maintenance => "info",
        _ => impact switch
        {
            IncidentImpact.Critical or IncidentImpact.Major => "critical",
            IncidentImpact.Minor => "warning",
            _ => "info",
        },
    };

    /// <summary>The state an incident asserts for a component when staff did not say.</summary>
    public static ComponentStatus AssertedStatus(IncidentImpact impact, IncidentKind kind) => kind switch
    {
        IncidentKind.Maintenance => ComponentStatus.UnderMaintenance,
        _ => impact switch
        {
            IncidentImpact.Critical => ComponentStatus.MajorOutage,
            IncidentImpact.Major => ComponentStatus.PartialOutage,
            IncidentImpact.Minor => ComponentStatus.DegradedPerformance,
            _ => ComponentStatus.Operational,
        },
    };
}
