namespace Identity.Contracts.Bus.Response;

public class GetVoipTokenForUserIdResponse
{
    public ICollection<string> Tokens { get; set; } = new List<string>();
}
