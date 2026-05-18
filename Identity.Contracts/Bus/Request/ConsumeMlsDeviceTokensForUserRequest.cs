namespace Identity.Contracts.Bus.Request;

public class ConsumeMlsDeviceTokensForUserRequest
{
    public ICollection<string> UserIds { get; set; }
   
}