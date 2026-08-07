namespace Guild.Application.Dtos.Response;

public class AbsenceDto
{
    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string UserId { get; set; } = null!;
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public string? Note { get; set; }
    public string CreatedByUserId { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What declaring or amending an absence actually did.</summary>
public class AbsenceSavedDto
{
    public required AbsenceDto Absence { get; init; }
    public required int ChoresReassigned { get; init; }
}

// PresentDays lives on ChoreBalanceEntryDto in Dtos/Response/HouseholdDtos.cs.
