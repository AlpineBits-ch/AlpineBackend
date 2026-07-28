namespace Identity.Contracts.Bus.Request;

public class GetVoipTokenForUserIdRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
