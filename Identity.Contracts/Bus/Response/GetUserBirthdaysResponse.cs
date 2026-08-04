namespace Identity.Contracts.Bus.Response;

public class GetUserBirthdaysResponse
{
    public ICollection<UserBirthdaySummary> Birthdays { get; set; } = new List<UserBirthdaySummary>();
}

/// <summary>
/// One account's date of birth, or the absence of one.
///
/// <para><see cref="BirthDate"/> is null in four different situations and the caller is not told
/// which: the account has no recorded date (every bot account), the date was erased by the account
/// purge (T1-9), the id does not exist here at all, or the account's <c>BirthdayVisibility</c> is
/// <c>Nobody</c> so no viewer may ever see it. Collapsing them is deliberate - a caller able to tell
/// "hidden" from "never recorded" learns something about the account it was refused.</para>
/// </summary>
public class UserBirthdaySummary
{
    public string UserId { get; set; } = null!;

    /// <summary>Null means "nothing to show", never "no restriction".</summary>
    public DateOnly? BirthDate { get; set; }
}
