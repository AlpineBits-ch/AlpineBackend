using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Status;

public class CreateIncidentParams
{
    public IncidentKind Kind { get; init; } = IncidentKind.Incident;
    public required string Title { get; init; }
    public required string Body { get; init; }
    public IncidentImpact Impact { get; init; } = IncidentImpact.Minor;
    public IncidentStatus Status { get; init; } = IncidentStatus.Investigating;
    public DateTimeOffset? ScheduledFor { get; init; }
    public DateTimeOffset? ScheduledUntil { get; init; }
    public string? CreatedByUserId { get; init; }
}

/// <summary>One incident or one maintenance window, with its timeline.</summary>
public class StatusIncident : BaseEntity<StatusIncident>, IPrefixedEntity
{
    public static string Prefix => "incd";

    public const int MaxTitleLength = 200;
    public const int MaxDetectionDetailLength = 2000;

    public IncidentKind Kind { get; set; } = IncidentKind.Incident;

    /// <summary>The quotable code, <c>VNT-XXXXXXXX</c>.</summary>
    public string Reference { get; set; } = null!;

    public string Title { get; set; } = null!;
    public IncidentImpact Impact { get; set; } = IncidentImpact.Minor;
    public IncidentStatus Status { get; set; } = IncidentStatus.Investigating;
    public IncidentOrigin Origin { get; set; } = IncidentOrigin.Manual;

    /// <summary>Null for staff-written incidents.</summary>
    public string? Template { get; set; }

    /// <summary>The component a generated incident was opened for.</summary>
    public string? AutoComponentId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? ScheduledUntil { get; set; }

    /// <summary>A human has taken this generated incident over. One-way.</summary>
    public bool Confirmed { get; set; }

    /// <summary>
    /// Hidden from every public read, still present in the table and in the console.
    /// </summary>
    public bool IsRetracted { get; set; }

    /// <summary>Staff-only.</summary>
    public string? DetectionDetail { get; set; }

    public string? CreatedByUserId { get; set; }

    public ICollection<StatusIncidentComponent> Components { get; set; } = new List<StatusIncidentComponent>();
    public ICollection<StatusIncidentUpdate> Updates { get; set; } = new List<StatusIncidentUpdate>();

    public bool IsOpen => ResolvedAt is null
                          && Status is not (IncidentStatus.Resolved or IncidentStatus.Completed or IncidentStatus.Cancelled);

    /// <summary>The detector may only close what it opened, and only while nobody else has.</summary>
    public bool CanBeAutomaticallyClosed => Origin == IncidentOrigin.Automatic && !Confirmed;

    public static StatusIncident Create(CreateIncidentParams p, DateTimeOffset now)
    {
        var title = p.Title.Trim();

        var incident = new StatusIncident
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            Kind = p.Kind,
            Reference = PublicReference.New(),
            Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title,
            Impact = p.Impact,
            Status = p.Status,
            Origin = IncidentOrigin.Manual,
            StartedAt = p.ScheduledFor ?? now,
            ScheduledFor = p.ScheduledFor,
            ScheduledUntil = p.ScheduledUntil,
            CreatedByUserId = p.CreatedByUserId,
            // A staff-written incident is owned by a person from the first keystroke.
            Confirmed = true,
        };

        incident.PostUpdate(p.Status, p.Body, template: null, p.CreatedByUserId, now);

        return incident;
    }

    /// <summary>Opens an incident the detector decided on.</summary>
    public static StatusIncident CreateAutomatic(
        StatusComponent component, string template, IncidentImpact impact, string detectionDetail,
        DateTimeOffset now)
    {
        var incident = new StatusIncident
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            Kind = IncidentKind.Incident,
            Reference = PublicReference.New(),
            Title = IncidentTemplates.Title(template, component),
            Impact = impact,
            Status = IncidentStatus.Investigating,
            Origin = IncidentOrigin.Automatic,
            Template = template,
            AutoComponentId = component.Id,
            StartedAt = now,
            DetectionDetail = Truncate(detectionDetail),
        };

        incident.PostUpdate(
            IncidentStatus.Investigating, IncidentTemplates.Body(template, component), template,
            authorUserId: null, now);

        return incident;
    }

    /// <summary>Appends to the timeline and moves the lifecycle state.</summary>
    public StatusIncidentUpdate PostUpdate(
        IncidentStatus status, string body, string? template, string? authorUserId, DateTimeOffset now)
    {
        var update = StatusIncidentUpdate.Create(Id, status, body, template, authorUserId, now);
        Updates.Add(update);

        Status = status;
        UpdatedAt = now;

        if (status is IncidentStatus.Resolved or IncidentStatus.Completed or IncidentStatus.Cancelled)
        {
            ResolvedAt ??= now;
        }
        else
        {
            // A reopen has to clear this or the incident stays filtered out of every "currently
            // active" query while showing an in-flight status.
            ResolvedAt = null;
        }

        if (authorUserId is not null) Confirmed = true;

        return update;
    }

    /// <summary>Marks the incident as belonging to a person.</summary>
    public void Confirm(DateTimeOffset now)
    {
        if (Confirmed) return;

        Confirmed = true;
        UpdatedAt = now;
    }

    public void Retract(DateTimeOffset now)
    {
        IsRetracted = true;
        UpdatedAt = now;
    }

    /// <summary>Replaces what this incident claims about which components.</summary>
    public void SetComponents(IEnumerable<(string ComponentId, ComponentStatus Status)> components, DateTimeOffset now)
    {
        var wanted = components.ToDictionary(c => c.ComponentId, c => c.Status, StringComparer.Ordinal);

        foreach (var existing in Components.ToList())
        {
            if (wanted.TryGetValue(existing.ComponentId, out var status))
            {
                existing.Status = status;
                wanted.Remove(existing.ComponentId);
                continue;
            }

            Components.Remove(existing);
        }

        foreach (var (componentId, status) in wanted)
        {
            Components.Add(StatusIncidentComponent.Create(Id, componentId, status));
        }

        UpdatedAt = now;
    }

    public void AppendDetectionDetail(string line, DateTimeOffset now)
    {
        DetectionDetail = Truncate(string.IsNullOrEmpty(DetectionDetail)
            ? line
            : $"{DetectionDetail}\n{line}");

        UpdatedAt = now;
    }

    private static string Truncate(string value) =>
        value.Length > MaxDetectionDetailLength ? value[..MaxDetectionDetailLength] : value;
}

/// <summary>Which components an incident asserts something about, and what it asserts.</summary>
public class StatusIncidentComponent
{
    public string IncidentId { get; set; } = null!;
    public StatusIncident? Incident { get; set; }

    public string ComponentId { get; set; } = null!;
    public StatusComponent? Component { get; set; }

    /// <summary>The state this incident claims for the component while it is open.</summary>
    public ComponentStatus Status { get; set; } = ComponentStatus.DegradedPerformance;

    public static StatusIncidentComponent Create(string incidentId, string componentId, ComponentStatus status) =>
        new() { IncidentId = incidentId, ComponentId = componentId, Status = status };
}

/// <summary>One entry on the timeline. Never edited, never deleted.</summary>
public class StatusIncidentUpdate : BaseEntity<StatusIncidentUpdate>, IPrefixedEntity
{
    public static string Prefix => "incu";

    public const int MaxBodyLength = 4000;

    public string IncidentId { get; set; } = null!;
    public StatusIncident? Incident { get; set; }

    /// <summary>The lifecycle state as of this entry.</summary>
    public IncidentStatus Status { get; set; }

    public string Body { get; set; } = string.Empty;

    public string? Template { get; set; }

    /// <summary>Null when the detector wrote it.</summary>
    public string? AuthorUserId { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public static StatusIncidentUpdate Create(
        string incidentId, IncidentStatus status, string body, string? template, string? authorUserId,
        DateTimeOffset now)
    {
        var text = body.Trim();

        return new StatusIncidentUpdate
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            IncidentId = incidentId,
            Status = status,
            Body = text.Length > MaxBodyLength ? text[..MaxBodyLength] : text,
            Template = template,
            AuthorUserId = authorUserId,
            PostedAt = now,
        };
    }
}

/// <summary>The only prose the detector is allowed to produce.</summary>
public static class IncidentTemplates
{
    public const string ElevatedErrors = "elevated_errors";
    public const string Unavailable = "unavailable";
    public const string Recovered = "recovered";

    public static string Title(string template, StatusComponent component) => template switch
    {
        Unavailable => $"{component.Name} is unavailable",
        _ => $"Elevated error rates affecting {component.Name}",
    };

    public static string Body(string template, StatusComponent component) => template switch
    {
        Recovered => $"{component.Name} is operating normally again. We are continuing to watch it.",
        _ => $"{component.ImpactHint} We are investigating.".TrimStart(),
    };
}
