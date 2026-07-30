using Identity.Application.Services.Qr;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

public class QrLoginStartedDto
{
    public string Code { get; set; } = null!;
    public int ExpiresInSeconds { get; set; }
}

public class QrLoginStatusDto
{
    public QrPairingStatus Status { get; set; }
}

public class QrLoginDeviceInfoDto
{
    public string DeviceName { get; set; } = null!;
    public DeviceType DeviceType { get; set; }
}
