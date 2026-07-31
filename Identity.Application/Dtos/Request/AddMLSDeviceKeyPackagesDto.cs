namespace Identity.Application.Dtos.Request;

public class AddMLSDeviceKeyPackagesDto
{
    public ICollection<PackageDto> KeyPackages { get; set; } = new List<PackageDto>();
}

public class PackageDto
{
    public byte[] KeyPackage { get; set; } = null!;

    /// <summary>When this package's own MLS lifetime runs out. Optional - omitting it falls back to
    /// <see cref="Identity.Domain.Entities.UserKeyPackage.DefaultLifetime"/>, which is deliberately
    /// conservative. Sending the real value stops the server retiring a package early.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Marks this as the device's reusable package of last resort. See
    /// <see cref="Identity.Domain.Entities.UserKeyPackage.IsLastResort"/> - a device should upload
    /// exactly one and re-upload it before it expires.</summary>
    public bool IsLastResort { get; set; }
}
