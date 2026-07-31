namespace Identity.Contracts.Bus.Response;

public class ValidateUserDeviceResponse
{
    /// <summary>True only for a device row that belongs to the requested user and is still
    /// active.</summary>
    public bool IsRegistered { get; set; }

    public string? DeviceName { get; set; }
}
