namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What is each of these accounts' date of birth?" - Identity owns it (privacy spec T2-17), Social
/// renders it behind <c>BirthdayVisibility</c>.
/// </summary>
public class GetUserBirthdaysRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
