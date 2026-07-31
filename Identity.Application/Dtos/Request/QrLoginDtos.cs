using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

public class StartQrLoginDto
{
    public string DeviceName { get; set; } = null!;
    public DeviceType DeviceType { get; set; }

    /// <summary>Optional.</summary>
    public string? ClientDeviceId { get; set; }
}

public class QrLoginCodeDto
{
    public string Code { get; set; } = null!;
}

public class ApproveQrLoginDto
{
    public string Code { get; set; } = null!;
    public bool Approve { get; set; }
}
