namespace Identity.Contracts.Bus.Response;

public class ConsumeMlsDeviceTokensForUserResponse
{
    public ICollection<DeviceTokenResponse> DeviceTokens { get; set; } = new List<DeviceTokenResponse>();

    /// <summary>Active devices that had no usable key package left, so the caller could not add them
    /// to the group. Surfaced rather than swallowed: these devices will never receive a Welcome and
    /// will never be able to read the conversation, and the user needs to be told that.</summary>
    public ICollection<UnreachableDeviceResponse> UnreachableDevices { get; set; } = new List<UnreachableDeviceResponse>();
}

public class UnreachableDeviceResponse
{
    public string UserId { get; set; } = null!;

    /// <summary>Client device id, matching <see cref="DeviceTokenResponse.DeviceId"/>.</summary>
    public string DeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;
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