namespace Identity.Contracts.Bus.Request;

public class GetDeviceTokenForUserIdRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}