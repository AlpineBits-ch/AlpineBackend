namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "Is this client device id one of this user's registered devices?" - Identity owns the device
/// table, so the services that read <c>X-Device-Id</c> off a request (Messaging voice/calls, Guild
/// voice) have to ask. Answers are cached briefly by the caller; see Echo.Realtime's
/// DeviceIdResolver.
/// </summary>
public class ValidateUserDeviceRequest
{
    public string UserId { get; set; } = null!;
    public string ClientDeviceId { get; set; } = null!;
}
