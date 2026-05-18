namespace Identity.Contracts.Bus.Response;

public class ConsumeMlsDeviceTokensForUserResponse
{
    public ICollection<DeviceTokenResponse> DeviceTokens { get; set; } = new List<DeviceTokenResponse>();
}

public class DeviceTokenResponse
{
    public string UserId { get; set; }
    public string DeviceId { get; set; }
    public byte[] Token { get; set; }
    
    public override string ToString()
    {
        return $"DeviceTokenResponse: UserId={UserId}, DeviceId={DeviceId}, TokenLength={Token?.Length ?? 0}";
    }
}