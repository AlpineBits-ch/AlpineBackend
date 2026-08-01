namespace Identity.Contracts.Bus.Response;

public class GetUserDevicesResponse
{
    public ICollection<UserDeviceSummaryResponse> Devices { get; set; } = new List<UserDeviceSummaryResponse>();
}

/// <summary>One active device.</summary>
public class UserDeviceSummaryResponse
{
    public string UserId { get; set; } = null!;

    /// <summary>The client device id, which is what Welcomes and per-device pushes are addressed to.</summary>
    public string ClientDeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;

    /// <summary>True when the device holds an unexpired account-signed certificate.</summary>
    public bool HasValidCertificate { get; set; }

    public DateTimeOffset? LastSeen { get; set; }
}
