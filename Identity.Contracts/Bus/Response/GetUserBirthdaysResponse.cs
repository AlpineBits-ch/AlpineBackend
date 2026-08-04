namespace Identity.Contracts.Bus.Response;

public class GetUserBirthdaysResponse
{
    public ICollection<UserBirthdaySummary> Birthdays { get; set; } = new List<UserBirthdaySummary>();
}

/// <summary>One account's date of birth, or the absence of one.</summary>
public class UserBirthdaySummary
{
    public string UserId { get; set; } = null!;

    /// <summary>Null means "nothing to show", never "no restriction".</summary>
    public DateOnly? BirthDate { get; set; }
}
