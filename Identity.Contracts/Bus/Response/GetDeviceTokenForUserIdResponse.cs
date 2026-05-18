namespace Identity.Contracts.Bus.Response;

public class GetDeviceTokenForUserIdResponse
{
    public ICollection<string> Tokens { get; set; } = new List<string>();
}