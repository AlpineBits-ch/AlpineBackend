using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

public class CreateMLSDeviceDto
{
    public string DeviceName { get; set; }
    public DeviceType DeviceType { get; set; }
    public byte[] IdentityPublicKey { get; set; }
    public string ClientDeviceId { get; set; }
}