namespace Guild.Application.Dtos.Request;

public class CreateAbsenceDto
{
    public DateTimeOffset StartAt { get; set; }

    /// <summary>Exclusive - an absence ending at 09:00 does not cover a chore due at 09:00.</summary>
    public DateTimeOffset EndAt { get; set; }

    /// <summary>"In Lisbon". Optional, and shown to the house.</summary>
    public string? Note { get; set; }
}

public class UpdateAbsenceDto
{
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }

    /// <summary>Sent as an explicit flag because null means "leave the note alone" while removing
    /// one has to be expressible too - the same shape as UpdateListItemDto.ClearAssignee.</summary>
    public bool? ClearNote { get; set; }

    public string? Note { get; set; }
}
